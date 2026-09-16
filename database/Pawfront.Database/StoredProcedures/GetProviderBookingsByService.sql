-- Levels ONE and TWO of the provider's PawPrints "Bookings" and "Earnings" cards:
-- the headline figures for a date range, and the per-service breakdown behind
-- them. Backs GET /providers/{id}/analytics/bookings and the [services] block now
-- returned by GET /providers/{id}/earnings. Level three is the existing
-- [Booking].[ListProviderEarningsBookings], which gained an @ServiceId filter so
-- the drill-down can name the service the caller tapped.
--
-- ONE PROCEDURE FOR BOOKING COUNTS AND MONEY, not two, because they come from the
-- same rows: splitting them would mean two reads that could report a different
-- number of completed jobs than they priced. Both cards therefore receive both
-- blocks; each renders the half it shows.
--
-- Returns TWO result sets:
--   1. Provider-wide totals for the range.
--   2. One row per service the provider offers, same column vocabulary.
--
-- RESULT SET 2 IS DRIVEN FROM [Provider].[ProviderServices], so a service with no
-- bookings in range still appears with zeros -- "nobody booked day care" is the
-- fact a provider needs, and omitting the row would read as "no such service".
-- Inactive services are included and flagged: their past bookings are real money.
--
-- THE BREAKDOWN SUMS EXACTLY TO THE TOTALS, unlike the views summary (where a
-- view can legitimately name no service). [ServiceId] is NOT NULL on both booking
-- tables and foreign-keyed to [Provider].[ProviderServices], deactivated rows
-- included, so every booking lands in exactly one bucket. Anything that fails to
-- reconcile here is a bug, not a modelled gap.
--
-- Rows and money come from [Booking].[BookingAmounts]; see that function for what
-- an amount means, which date buckets it, and how an unpaid booking is priced.
-- The aggregate expressions are deliberately IDENTICAL to
-- [Booking].[GetProviderEarningsSummary]'s -- change one and you must change the
-- other, or a provider's dashboard card will disagree with its own breakdown.
--
-- THE COUNTS PARTITION THE RANGE TWICE OVER: every platform booking is exactly
-- one of [CompletedBookings] (COMPLETED / PAID), [UpcomingBookings] (still in
-- flight) or the unrealised trio, so those four sum to [TotalBookings]; and it is
-- also exactly one of [PendingBookings], [AcceptedBookings] or the unrealised
-- trio, which is the split the dashboard's own card asks for ("how many jobs have
-- I taken on", where an unanswered request is not one). They match the API's
-- status groups one for one, so a client can tap a figure and list exactly those
-- rows via ?status=. Custom walk-ins are excluded from all of them and reported as
-- [PrivateJob*], consistent with every other platform figure.
CREATE OR ALTER PROCEDURE [Booking].[GetProviderBookingsByService]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0
AS
BEGIN
    SET NOCOUNT ON;

    -- Materialised once so the totals and the breakdown are computed from exactly
    -- the same rows, and so the function is not evaluated twice.
    DECLARE @InRange TABLE
    (
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [IsEarned] BIT NOT NULL,
        [IsPaid] BIT NOT NULL,
        [IsPrivate] BIT NOT NULL,
        [Amount] DECIMAL(12, 2) NULL,
        [Fee] DECIMAL(12, 2) NULL
    );

    INSERT INTO @InRange ([ServiceId], [Status], [IsEarned], [IsPaid], [IsPrivate], [Amount], [Fee])
    SELECT e.[ServiceId],
           e.[Status],
           e.[IsEarned],
           e.[IsPaid],
           e.[IsPrivate],
           e.[Amount],
           e.[Fee]
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);

    -- Result set 1: the headline figures.
    SELECT
        [TotalBookings]           = COUNT(CASE WHEN r.[IsPrivate] = 0 THEN 1 END),
        [CompletedBookings]       = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN 1 END),
        [PaidBookings]            = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN 1 END),
        [AwaitingPaymentBookings] = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN 1 END),
        [UnpricedBookings]        = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[Amount] IS NULL THEN 1 END),
        -- Still in flight: neither earned nor failed. Defined as the COMPLEMENT of
        -- the other two groups rather than as its own status list, so a lifecycle
        -- status added later cannot fall through every bucket and quietly vanish
        -- from [TotalBookings].
        [UpcomingBookings]        = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[IsEarned] = 0
                                                AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                       N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                       N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                               THEN 1 END),

        [GrossAmount]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN r.[Amount] END), 0),
        [PawfrontFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN r.[Fee] END), 0),
        [ReceivedGross] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN r.[Amount] END), 0),
        [ReceivedFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN r.[Fee] END), 0),
        [AwaitingGross] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN r.[Amount] END), 0),
        [AwaitingFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN r.[Fee] END), 0),

        [PrivateJobCount]  = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 1 THEN 1 END),
        [PrivateJobAmount] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 1 THEN r.[Amount] END), 0),

        [CancelledJobCount]  = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN 1 END),
        [CancelledJobAmount] = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN r.[Amount] END), 0),
        [NoShowJobCount]     = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN 1 END),
        [NoShowJobAmount]    = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN r.[Amount] END), 0),
        [ExpiredJobCount]    = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [ExpiredJobAmount]   = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN r.[Amount] END), 0),

        -- APPENDED LAST so no existing reader ordinal moved. These three split
        -- [TotalBookings] the way the dashboard's outer card actually asks about
        -- it: total = [PendingBookings] + [AcceptedBookings] + the unrealised
        -- trio. A request the provider has not answered yet is NOT work they
        -- have taken on, so a card counting "my jobs" must not include it.
        [PendingBookings]     = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'CREATED', N'APPROVAL_NEEDED') THEN 1 END),
        -- Accepted and not fallen through: confirmed-equivalent, underway, and
        -- finished alike. Defined as the COMPLEMENT of pending + unrealised
        -- rather than as its own status list, for the same reason
        -- [UpcomingBookings] is — a status added later cannot fall through every
        -- bucket and quietly leave the partition short.
        [AcceptedBookings]    = COUNT(CASE WHEN r.[IsPrivate] = 0
                                            AND r.[Status] NOT IN (N'CREATED', N'APPROVAL_NEEDED')
                                            AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                       N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                       N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                           THEN 1 END),
        -- The private half of the same question. A walk-in is CONFIRMED from the
        -- moment the provider records it (there is nobody to accept it), so
        -- "accepted" here means only "not cancelled". Distinct from
        -- [PrivateJobCount], which is gated on IsEarned because it feeds a money
        -- figure; this one feeds a job COUNT, where a walk-in the provider has
        -- taken on but not finished still counts.
        [PrivateAcceptedJobs] = COUNT(CASE WHEN r.[IsPrivate] = 1
                                            AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                       N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                       N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                           THEN 1 END)
    FROM @InRange r;

    -- Result set 2: the same figures per service, services with zeros included.
    SELECT s.[ServiceId],
           s.[ServiceCategory],
           s.[SubCategory],
           s.[ServiceType],
           s.[IsActive],

           [TotalBookings]           = COUNT(CASE WHEN r.[IsPrivate] = 0 THEN 1 END),
           [CompletedBookings]       = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN 1 END),
           [PaidBookings]            = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN 1 END),
           [AwaitingPaymentBookings] = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN 1 END),
           [UnpricedBookings]        = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[Amount] IS NULL THEN 1 END),
           [UpcomingBookings]        = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[IsEarned] = 0
                                                   AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                          N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                                  THEN 1 END),

           [GrossAmount]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN r.[Amount] END), 0),
           [PawfrontFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN r.[Fee] END), 0),
           [ReceivedGross] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN r.[Amount] END), 0),
           [ReceivedFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 1 THEN r.[Fee] END), 0),
           [AwaitingGross] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN r.[Amount] END), 0),
           [AwaitingFee]   = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 AND r.[IsPaid] = 0 THEN r.[Fee] END), 0),

           [PrivateJobCount]  = COUNT(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 1 THEN 1 END),
           [PrivateJobAmount] = ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 1 THEN r.[Amount] END), 0),

           [CancelledJobCount]  = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN 1 END),
           [CancelledJobAmount] = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN r.[Amount] END), 0),
           [NoShowJobCount]     = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN 1 END),
           [NoShowJobAmount]    = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN r.[Amount] END), 0),
           [ExpiredJobCount]    = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
           [ExpiredJobAmount]   = ISNULL(SUM(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN r.[Amount] END), 0),

           -- The same three as result set 1, in the same order -- both sets are
           -- read by ONE C# method at different offsets, so they must stay
           -- column-for-column identical.
           [PendingBookings]     = COUNT(CASE WHEN r.[IsPrivate] = 0 AND r.[Status] IN (N'CREATED', N'APPROVAL_NEEDED') THEN 1 END),
           [AcceptedBookings]    = COUNT(CASE WHEN r.[IsPrivate] = 0
                                               AND r.[Status] NOT IN (N'CREATED', N'APPROVAL_NEEDED')
                                               AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                       N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                       N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                              THEN 1 END),
           [PrivateAcceptedJobs] = COUNT(CASE WHEN r.[IsPrivate] = 1
                                               AND r.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                                                       N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                                                       N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
                                              THEN 1 END)
    FROM [Provider].[ProviderServices] s
    LEFT JOIN @InRange r
        ON r.[ServiceId] = s.[ServiceId]
    WHERE s.[ProviderId] = @ProviderId
    GROUP BY s.[ServiceId], s.[ServiceCategory], s.[SubCategory], s.[ServiceType], s.[IsActive]
    -- Active first (what they are selling now), then by money earned, then a
    -- stable tie-break so a client without paging renders consistently.
    ORDER BY s.[IsActive] DESC,
             ISNULL(SUM(CASE WHEN r.[IsEarned] = 1 AND r.[IsPrivate] = 0 THEN r.[Amount] END), 0) DESC,
             s.[ServiceType];
END;

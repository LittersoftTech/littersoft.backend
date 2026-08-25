-- Aggregate earnings for one provider over an optional service-date range
-- (@FromDate / @ToDate inclusive; both NULL = all time). Backs BOTH the provider
-- earnings overview and the period-filtered earnings endpoint — the two differ
-- only in the range they pass, so their numbers can never drift apart.
--
-- Rows and money come from [Booking].[BookingAmounts]; see that function for what
-- an amount means, which date buckets it, and how an unpaid booking is priced.
--
-- TWO GROUPS OF FIGURES, and they must not be confused:
--   * EARNED ([IsEarned] = COMPLETED / PAID) — the money. Gross / Fee / Received /
--     Awaiting and their counts all gate on it, so a cancelled or upcoming booking
--     can never inflate what the provider is owed.
--   * UNREALISED ([Cancelled*] / [NoShow*] / [Expired*]) — jobs that were on the
--     books and produced nothing. Added because a payouts screen has to account for
--     the gap between what was booked and what was earned; without them a provider
--     seeing a thin month cannot tell whether they were quiet or were stood up.
--     Reported ALONGSIDE the earned figures and never folded into them: no money
--     moved on any of these, so adding them to Gross would misstate what the
--     provider holds. The three are disjoint and together cover exactly the raw
--     statuses the API's 'Cancelled' status group expands to:
--       Cancelled -> PROVIDER_CANCELLED, PARENT_CANCELLED, PROVIDER_DECLINED
--       NoShow    -> PARENT_NO_SHOW, PROVIDER_NO_SHOW
--       Expired   -> EXPIRED, JOB_EXPIRED, OTP_MAX_ATTEMPTS_EXCEEDED
--     [Amount] on such a row is what the job WOULD have been worth, priced from its
--     creation-time price-lock. No fee is reported against them: a commission on
--     money that never changed hands is not owed, so there is nothing to net off.
--
-- Bookings still in flight (CREATED / CONFIRMED / IN_PROGRESS / mid-modification)
-- are in NEITHER group — they have not happened yet and have not failed. The
-- booking list's 'Upcoming' status group is how a client asks for those.
--
-- Platform figures EXCLUDE Custom walk-ins throughout, unrealised ones included:
-- those are arranged off-platform and carry no commission, so folding them in
-- would misstate what Pawfront processed. They are reported separately as
-- [PrivateJob*] so the provider still sees the work rather than wondering where it
-- went. Note [PrivateJob*] stays gated on [IsEarned], so a walk-in that was
-- cancelled appears in no figure here.
--
-- Net is not returned — it is Gross - Fee and the caller computes it, so there is
-- one subtraction in the codebase rather than one here and another in C#.
CREATE OR ALTER PROCEDURE [Booking].[GetProviderEarningsSummary]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        -- Counts (earned platform bookings only).
        [CompletedBookings]       = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN 1 END),
        [PaidBookings]            = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN 1 END),
        [AwaitingPaymentBookings] = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN 1 END),
        -- Completed bookings we could not price at all (legacy rows created before
        -- price-lock whose offering has since gone). Surfaced rather than hidden so
        -- a provider can see the totals are incomplete instead of silently low.
        [UnpricedBookings]        = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[Amount] IS NULL THEN 1 END),

        -- Money (earned platform bookings only). Gross = what the parent pays;
        -- Fee = the Pawfront commission on it. With cash the provider physically
        -- holds Gross and owes Fee, so both matter to them.
        [GrossAmount]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN e.[Amount] END), 0),
        [PawfrontFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN e.[Fee] END), 0),
        [ReceivedGross] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Amount] END), 0),
        [ReceivedFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Fee] END), 0),
        [AwaitingGross] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Amount] END), 0),
        [AwaitingFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Fee] END), 0),

        -- Off-platform private jobs, reported alongside but never mixed in.
        [PrivateJobCount]  = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 1 THEN 1 END),
        [PrivateJobAmount] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 1 THEN e.[Amount] END), 0),

        -- Unrealised: booked, then nothing. Never part of the money above.
        [CancelledJobCount]  = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN 1 END),
        [CancelledJobAmount] = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN e.[Amount] END), 0),
        [NoShowJobCount]     = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN 1 END),
        [NoShowJobAmount]    = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN e.[Amount] END), 0),
        [ExpiredJobCount]    = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [ExpiredJobAmount]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN e.[Amount] END), 0)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);
END;

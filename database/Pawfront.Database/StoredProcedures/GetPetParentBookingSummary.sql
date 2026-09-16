-- Aggregate view of one pet parent's service bookings: how many, and how much they
-- spent. Optional filters — service-date range, a single pet, and a status set —
-- apply to EVERY figure below, so the summary always describes exactly the same
-- set of bookings the paginated history endpoint is showing.
--
-- @Statuses is a comma-separated list of raw lifecycle statuses (the API expands
-- friendly groups like 'Completed' / 'Upcoming' / 'Cancelled' into it, so the
-- grouping vocabulary lives in one C# place instead of being duplicated here).
-- NULL or empty means no status filter.
--
-- Money comes from [Booking].[BookingAmounts] — the same function the provider
-- earnings sprocs use, so a parent's "spent" on a booking always equals the
-- provider's "earned" on it.
--
-- Custom walk-ins never appear: they carry no PetParentId.
CREATE OR ALTER PROCEDURE [Booking].[GetPetParentBookingSummary]
    @PetParentId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @PetId UNIQUEIDENTIFIER = NULL,
    @Statuses NVARCHAR(MAX) = NULL,
    @FeePercentage DECIMAL(9, 4) = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        [TotalBookings]           = COUNT(*),
        [SingleDayBookings]       = COUNT(CASE WHEN e.[BookingType] = N'SingleDay' THEN 1 END),
        [NightStayBookings]       = COUNT(CASE WHEN e.[BookingType] = N'NightStay' THEN 1 END),

        -- The three buckets a parent actually thinks in. They are mutually
        -- exclusive and sum to [TotalBookings]: a booking either happened
        -- (completed), is still going to (upcoming), or fell through (cancelled —
        -- including declines, no-shows and expiries, which from the parent's side
        -- all mean "this didn't happen").
        [CompletedBookings]       = COUNT(CASE WHEN e.[IsEarned] = 1 THEN 1 END),
        [CancelledBookings]       = COUNT(CASE WHEN e.[IsEarned] = 0 AND e.[Status] IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [UpcomingBookings]        = COUNT(CASE WHEN e.[IsEarned] = 0 AND e.[Status] NOT IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),

        [PaidBookings]            = COUNT(CASE WHEN e.[IsPaid] = 1 THEN 1 END),
        -- Completed but the provider hasn't recorded the payment yet. Counted as
        -- spent all the same (the parent already handed over the cash), so this is
        -- informational rather than a deduction.
        [AwaitingPaymentBookings] = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPaid] = 0 THEN 1 END),
        -- Bookings with no resolvable price (legacy rows created before price-lock);
        -- surfaced so a low total is visibly incomplete rather than silently wrong.
        [UnpricedBookings]        = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[Amount] IS NULL THEN 1 END),

        [AmountSpent]    = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 THEN e.[Amount] END), 0),
        -- What the parent is committed to but hasn't been served yet — not part of
        -- [AmountSpent], since no money has moved.
        [UpcomingAmount] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 0 AND e.[Status] NOT IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN e.[Amount] END), 0)
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')));
END;

-- Aggregate earnings for one provider over an optional service-date range
-- (@FromDate / @ToDate inclusive; both NULL = all time). Backs BOTH the provider
-- earnings overview and the period-filtered earnings endpoint — the two differ
-- only in the range they pass, so their numbers can never drift apart.
--
-- Rows and money come from [Booking].[BookingAmounts]; see that function for what
-- an amount means, which date buckets it, and how an unpaid booking is priced.
-- Only [IsEarned] rows (COMPLETED / PAID) contribute — a cancelled or upcoming
-- booking is not earnings.
--
-- Platform earnings EXCLUDE Custom walk-ins: those are arranged off-platform and
-- carry no commission, so folding them in would misstate what Pawfront processed.
-- They are reported separately as [PrivateJob*] so the provider still sees the
-- work rather than wondering where it went.
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
        -- Counts (platform bookings only).
        [CompletedBookings]       = COUNT(CASE WHEN e.[IsPrivate] = 0 THEN 1 END),
        [PaidBookings]            = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN 1 END),
        [AwaitingPaymentBookings] = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN 1 END),
        -- Completed bookings we could not price at all (legacy rows created before
        -- price-lock whose offering has since gone). Surfaced rather than hidden so
        -- a provider can see the totals are incomplete instead of silently low.
        [UnpricedBookings]        = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Amount] IS NULL THEN 1 END),

        -- Money (platform bookings only). Gross = what the parent pays; Fee = the
        -- Pawfront commission on it. With cash the provider physically holds Gross
        -- and owes Fee, so both matter to them.
        [GrossAmount]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 THEN e.[Amount] END), 0),
        [PawfrontFee]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 THEN e.[Fee] END), 0),
        [ReceivedGross] = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Amount] END), 0),
        [ReceivedFee]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Fee] END), 0),
        [AwaitingGross] = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Amount] END), 0),
        [AwaitingFee]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Fee] END), 0),

        -- Off-platform private jobs, reported alongside but never mixed in.
        [PrivateJobCount]  = COUNT(CASE WHEN e.[IsPrivate] = 1 THEN 1 END),
        [PrivateJobAmount] = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 1 THEN e.[Amount] END), 0)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE e.[IsEarned] = 1
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);
END;

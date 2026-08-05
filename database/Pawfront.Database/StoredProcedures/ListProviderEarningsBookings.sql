-- Booking-level breakdown behind a provider's earnings figure: which jobs
-- produced what. Paginated (the API caps @Take at 20), filterable by service-date
-- range, sortable by date or by amount in either direction.
--
-- Returns TWO result sets:
--   1. [TotalCount] — matching rows before paging, so the client can page.
--   2. The page itself.
--
-- Rows come from [Booking].[BookingAmounts] under the same [IsEarned] + date
-- filters as [Booking].[GetProviderEarningsSummary], so the list reconciles
-- exactly with the summary over the same range. Custom walk-ins ARE listed (they
-- are real work the provider did) but carry [IsPrivate] = 1 and are excluded from
-- the summary's platform totals — the flag is what lets the client present them
-- apart rather than silently breaking the reconciliation.
CREATE OR ALTER PROCEDURE [Booking].[ListProviderEarningsBookings]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Earnings'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [TotalCount] = COUNT(*)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE e.[IsEarned] = 1
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);

    SELECT
        e.[BookingType],
        e.[BookingId],
        -- Raw job number; the 'PF-000123' label is formatted in C# exactly as the
        -- booking-detail read does, so the two surfaces show the same id.
        [JobNumber]       = COALESCE(b.[JobNumber], n.[JobNumber]),
        [PayoutId]        = COALESCE(b.[PayoutId], n.[PayoutId]),
        [PayoutStatus]    = COALESCE(b.[PayoutStatus], n.[PayoutStatus]),
        e.[Status],
        [ServiceCategory] = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
        [SubCategory]     = COALESCE(b.[SubCategory], n.[SubCategory]),
        [ServiceItemCode] = b.[ServiceItemCode],
        e.[ServiceDate],
        -- Single-day shape (NULL on a stay).
        [StartTime]       = b.[StartTime],
        [EndTime]         = b.[EndTime],
        -- Night-stay shape (NULL on a single-day booking).
        [CheckInDate]     = n.[CheckInDate],
        [CheckOutDate]    = n.[CheckOutDate],
        [Nights]          = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                 THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
        -- App bookings name the parent from their profile; a Custom walk-in carries
        -- its own free-text customer instead.
        [CustomerName]    = COALESCE(pp.[FirstName] + N' ' + pp.[LastName], b.[CustomerName]),
        [PetName]         = COALESCE(pet.[PetName], b.[PetName]),
        e.[IsPaid],
        e.[IsPrivate],
        e.[Amount],
        e.[Fee],
        e.[PaidAtUtc],
        e.[PaymentMethod]
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    LEFT JOIN [Booking].[Bookings] b
        ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = e.[PetParentId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = e.[PetId]
    WHERE e.[IsEarned] = 1
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
    ORDER BY
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Asc'  THEN e.[Amount] END ASC,
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Desc' THEN e.[Amount] END DESC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Asc'  THEN e.[ServiceDate] END ASC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Desc' THEN e.[ServiceDate] END DESC,
        -- Deterministic tie-break. Without it, rows sharing a date (or an amount)
        -- can be ordered differently between two calls, which makes OFFSET paging
        -- repeat or skip rows.
        e.[BookingId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

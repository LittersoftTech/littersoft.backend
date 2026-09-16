-- Booking-level breakdown behind a provider's earnings figure: which jobs
-- produced what — and, when asked, which produced nothing. Paginated (the API
-- caps @Take at 20), filterable by service-date range and by status, sortable by
-- date or by amount in either direction.
--
-- Returns TWO result sets:
--   1. [TotalCount] — matching rows before paging, so the client can page.
--   2. The page itself.
--
-- @Statuses is a comma-separated list of raw lifecycle statuses (the API expands
-- its friendly 'Completed' / 'Upcoming' / 'Cancelled' / 'NoShow' / 'Expired'
-- groups into raw statuses before calling, exactly as the pet-parent history
-- sprocs take them — so adding a lifecycle state means editing the C# vocabulary
-- rather than four stored procedures).
--
-- WHEN @Statuses IS OMITTED the rows are the [IsEarned] ones (COMPLETED / PAID),
-- under the same date filters as [Booking].[GetProviderEarningsSummary] — so the
-- default list still reconciles exactly with that summary's earned figures. This
-- default is deliberate rather than "return everything": it is what every existing
-- caller already relies on, and a list that silently started including cancelled
-- jobs would break that reconciliation without anybody asking it to.
--
-- WHEN @Statuses IS SUPPLIED it replaces the [IsEarned] gate entirely — the caller
-- has named the statuses they want, so second-guessing them would make it
-- impossible to ask for cancelled or no-showed jobs at all, which is the whole
-- point of the parameter.
--
-- @ServiceId narrows to ONE of the provider's bookable services, which is what
-- makes this the third level of the PawPrints drill-down: a provider taps a
-- service on the per-service breakdown ([Booking].[GetProviderBookingsByService])
-- and lands on exactly the jobs behind that figure. Omit it for the whole
-- provider, exactly as before.
--
-- [Breed] / [PetGender] / [CustomerPhotoUrl] are joined LIVE and appended LAST,
-- so no existing reader ordinal moved. They exist because this list IS the
-- customer level of the feature and had nothing but two names on it: a card
-- showing who booked, and which animal, previously needed a second call per row
-- to the booking detail. All three are NULL on a Custom walk-in, which has no
-- parent or pet record to join -- its free-text [CustomerName] / [PetName] are
-- already COALESCEd in below, and there is no breed or photo to be had.
--
-- Custom walk-ins ARE listed (they are real work the provider did) but carry
-- [IsPrivate] = 1 and are excluded from the summary's platform totals — the flag
-- is what lets the client present them apart rather than silently breaking the
-- reconciliation.
CREATE OR ALTER PROCEDURE [Booking].[ListProviderEarningsBookings]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Earnings'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20,
    -- NULL / empty = the earned rows only (back-compatible default, see header).
    @Statuses NVARCHAR(MAX) = NULL,
    -- NULL = every service, the pre-existing behaviour.
    @ServiceId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FilterByStatus BIT =
        CASE WHEN @Statuses IS NULL OR @Statuses = N'' THEN 0 ELSE 1 END;

    SELECT [TotalCount] = COUNT(*)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE ((@FilterByStatus = 0 AND e.[IsEarned] = 1)
           OR (@FilterByStatus = 1
               AND e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N','))))
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@ServiceId IS NULL OR e.[ServiceId] = @ServiceId);

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
        e.[PaymentMethod],
        -- Did this job produce money, or is it one of the unrealised ones? Emitted
        -- rather than left for the client to re-derive from [Status]: the moment a
        -- list can contain both, every row has to answer it, and deriving it in the
        -- app would be a second copy of a rule that already lives in the function.
        e.[IsEarned],
        -- The customer card. Live-joined rather than snapshotted, so a parent
        -- who deletes their account reads its anonymised placeholder here
        -- instead of leaving their real breed/photo in a provider's report.
        [Breed]            = pet.[Breed],
        [PetGender]        = pet.[Gender],
        [CustomerPhotoUrl] = pp.[ProfilePhotoUrl]
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    LEFT JOIN [Booking].[Bookings] b
        ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = e.[PetParentId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = e.[PetId]
    WHERE ((@FilterByStatus = 0 AND e.[IsEarned] = 1)
           OR (@FilterByStatus = 1
               AND e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N','))))
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@ServiceId IS NULL OR e.[ServiceId] = @ServiceId)
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

-- Paginated, filterable history of one pet parent's service bookings — single-day
-- and night-stay merged into one feed (a parent thinks "my bookings", not "my
-- two kinds of bookings"); [BookingType] tells them apart.
--
-- Same filter parameters, and the same defaults, as
-- [Booking].[GetPetParentBookingSummary] — the summary must describe exactly the
-- set this returns, so the two WHERE clauses are deliberately identical: change
-- one, change the other.
--
-- Returns TWO result sets:
--   1. [TotalCount] — matching rows before paging.
--   2. The page itself.
--
-- Unlike the provider earnings list this is NOT restricted to [IsEarned] rows:
-- cancelled and upcoming bookings belong in a history screen, each carrying its
-- expected [Amount] so the client can show what a cancelled booking would have
-- cost.
CREATE OR ALTER PROCEDURE [Booking].[ListPetParentBookingHistory]
    @PetParentId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @PetId UNIQUEIDENTIFIER = NULL,
    @Statuses NVARCHAR(MAX) = NULL,
    @FeePercentage DECIMAL(9, 4) = 0,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Amount'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [TotalCount] = COUNT(*)
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')));

    SELECT
        e.[BookingType],
        e.[BookingId],
        [JobNumber]        = COALESCE(b.[JobNumber], n.[JobNumber]),
        e.[Status],
        [ServiceId]        = COALESCE(b.[ServiceId], n.[ServiceId]),
        [ServiceCategory]  = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
        [SubCategory]      = COALESCE(b.[SubCategory], n.[SubCategory]),
        [ServiceItemCode]  = b.[ServiceItemCode],
        e.[ServiceDate],
        -- Single-day shape (NULL on a stay).
        [BookingDate]      = b.[BookingDate],
        [StartTime]        = b.[StartTime],
        [EndTime]          = b.[EndTime],
        -- Night-stay shape (NULL on a single-day booking).
        [CheckInDate]      = n.[CheckInDate],
        [CheckOutDate]     = n.[CheckOutDate],
        [Nights]           = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                  THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
        [ProviderId]       = e.[ProviderId],
        -- The provider's personal name from SQL. Their BUSINESS name lives in the
        -- Cosmos offering doc and is not joinable here — the booking-detail read
        -- takes the same approach, so the two screens agree.
        [ProviderName]     = pr.[FirstName] + N' ' + pr.[LastName],
        e.[PetId],
        [PetName]          = pet.[PetName],
        [PetProfilePhotoUrl] = pet.[ProfilePhotoUrl],
        e.[IsEarned],
        e.[IsPaid],
        -- Gross only. The Pawfront commission is the provider's concern — the
        -- parent pays this amount either way, so surfacing the split here would
        -- only invite "am I being charged extra?".
        e.[Amount],
        e.[PaidAtUtc],
        e.[PaymentMethod]
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    LEFT JOIN [Booking].[Bookings] b
        ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
    LEFT JOIN [Provider].[Providers] pr
        ON pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = e.[PetId]
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')))
    ORDER BY
        CASE WHEN @SortBy = N'Amount' AND @SortDirection = N'Asc'  THEN e.[Amount] END ASC,
        CASE WHEN @SortBy = N'Amount' AND @SortDirection = N'Desc' THEN e.[Amount] END DESC,
        CASE WHEN @SortBy <> N'Amount' AND @SortDirection = N'Asc'  THEN e.[ServiceDate] END ASC,
        CASE WHEN @SortBy <> N'Amount' AND @SortDirection = N'Desc' THEN e.[ServiceDate] END DESC,
        -- Deterministic tie-break; without it OFFSET paging can repeat or skip rows
        -- that share a date or an amount.
        e.[BookingId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

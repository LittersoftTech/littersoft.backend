-- The provider's JOB LIST -- the agenda/inbox screen behind their filter sheet.
-- One paginated feed covering BOTH booking kinds, filterable by job status,
-- service type, service location, animal type + breed, earnings range and date,
-- and sortable by date or by earnings.
--
-- WHY THIS EXISTS ALONGSIDE [Booking].[ListBookingsByProvider]: that one is
-- single-day only, unpaginated, and takes no filters. The filter sheet offers
-- "Day Care" AND "Night Stay" side by side, so the two tables have to be merged
-- into one feed -- and a provider selecting several statuses needs a total count
-- and a stable page, neither of which a bare array can give. The existing
-- procedure is left exactly as it is, so nothing the provider app already calls
-- changes.
--
-- IT READS [Booking].[BookingAmounts], the same function the earnings sprocs
-- read, deliberately: a job's money on this screen must be the same number the
-- earnings screen reports for it. That also means the Amount arithmetic, the
-- ledger-wins rule, and [IsEarned] / [IsPrivate] all come from one definition
-- rather than being restated here.
--
-- Returns TWO result sets:
--   1. [TotalCount] -- matching rows before paging, so the client can page.
--   2. The page itself.
--
-- WHICH DATE: unlike [Booking].[BookingAmounts], whose [ServiceDate] is the
-- EARNINGS date (checkout, for a stay), this procedure works from the date the
-- job STARTS -- [BookingDate] for a single-day booking, [CheckInDate] for a stay.
-- An agenda is a list of when the provider has to be somewhere, and for a
-- three-night stay that is the drop-off day. The two are deliberately different
-- answers to different questions; the earnings figure is unaffected either way.
--
-- The date FILTER is an overlap rather than an equality, so ?from=&to= naming a
-- single day returns a stay that spans it. It counts the CHECK-OUT DAY as part of
-- the stay (>= rather than >), which departs from the [CheckInDate, CheckOutDate)
-- convention the capacity queries use -- and on purpose: the checkout day is not
-- a stayed night, but it IS a day the provider has a hand-over to do, which is
-- exactly what an agenda is for.
--
-- @Statuses / @ServiceTypes / @AnimalTypes are comma-separated lists expanded in
-- C# (the friendly status groups live in Pawfront.Application's
-- BookingStatusFilter, so adding a lifecycle state means editing that file rather
-- than this one). NULL / empty means "no filter on that dimension" -- so the
-- default feed is EVERY job the provider has, which is what an inbox should open
-- on, and is the one place this differs in posture from
-- [Booking].[ListProviderEarningsBookings] (whose default is the earned rows,
-- because it has a summary to reconcile with).
--
-- The customer and pet columns are joined LIVE from [Parent].[PetParents] /
-- [Parent].[Pets] with the booking's own free text as the fallback, exactly as
-- the other two provider-facing lists do, so a deleted account reads
-- "Deleted User" / "Deleted Pet" rather than leaving real personal data frozen in
-- a list. They are NULL on a Custom walk-in beyond its own free text.
CREATE OR ALTER PROCEDURE [Booking].[ListProviderJobs]
    @ProviderId UNIQUEIDENTIFIER,
    @FeePercentage DECIMAL(9, 4) = 0,
    -- Raw lifecycle statuses, comma-separated. NULL / empty = every status.
    @Statuses NVARCHAR(MAX) = NULL,
    -- 'DayCare,NightStay,GroomingSession,TrainingSession,VetAppointment'.
    @ServiceTypes NVARCHAR(MAX) = NULL,
    -- One of the provider's own services, for a per-service view.
    @ServiceId UNIQUEIDENTIFIER = NULL,
    -- 'ParentLocation' (the design's "Customer Address") | 'ProviderLocation'
    -- ("Your Address").
    @LocationType NVARCHAR(32) = NULL,
    -- 'Dog,Cat,Hamster,GuineaPig'.
    @AnimalTypes NVARCHAR(MAX) = NULL,
    -- Case-insensitive "contains" match on the pet's breed.
    @Breed NVARCHAR(200) = NULL,
    -- Inclusive bounds on what the job is worth. A job with no price snapshot has
    -- a NULL Amount and is excluded by either bound -- it cannot be shown to
    -- satisfy a range it has no value for.
    @MinEarnings DECIMAL(12, 2) = NULL,
    @MaxEarnings DECIMAL(12, 2) = NULL,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Earnings'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    -- Build a case-insensitive "contains" LIKE pattern for the breed search, with
    -- LIKE metacharacters in the term escaped so they match literally. Same
    -- treatment [Event].[ListEvents] gives its title search.
    DECLARE @BreedPattern NVARCHAR(410) = NULL;
    IF (@Breed IS NOT NULL AND LTRIM(RTRIM(@Breed)) <> N'')
    BEGIN
        SET @BreedPattern = N'%' +
            REPLACE(REPLACE(REPLACE(REPLACE(
                LOWER(LTRIM(RTRIM(@Breed))),
                N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';
    END

    DECLARE @FilterByStatus BIT =
        CASE WHEN @Statuses IS NULL OR @Statuses = N'' THEN 0 ELSE 1 END;
    DECLARE @FilterByServiceType BIT =
        CASE WHEN @ServiceTypes IS NULL OR @ServiceTypes = N'' THEN 0 ELSE 1 END;
    DECLARE @FilterByAnimalType BIT =
        CASE WHEN @AnimalTypes IS NULL OR @AnimalTypes = N'' THEN 0 ELSE 1 END;

    -- Materialise the three list filters once rather than re-splitting them in
    -- both the count and the page query.
    DECLARE @StatusFilter TABLE ([Value] NVARCHAR(48) NOT NULL PRIMARY KEY);
    DECLARE @ServiceTypeFilter TABLE ([Value] NVARCHAR(64) NOT NULL PRIMARY KEY);
    DECLARE @AnimalTypeFilter TABLE ([Value] NVARCHAR(32) NOT NULL PRIMARY KEY);

    IF (@FilterByStatus = 1)
        INSERT INTO @StatusFilter ([Value])
        SELECT DISTINCT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')
        WHERE LTRIM(RTRIM([value])) <> N'';

    IF (@FilterByServiceType = 1)
        INSERT INTO @ServiceTypeFilter ([Value])
        SELECT DISTINCT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@ServiceTypes, N',')
        WHERE LTRIM(RTRIM([value])) <> N'';

    IF (@FilterByAnimalType = 1)
        INSERT INTO @AnimalTypeFilter ([Value])
        SELECT DISTINCT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@AnimalTypes, N',')
        WHERE LTRIM(RTRIM([value])) <> N'';

    -- One shape for both result sets. Everything the filters and the sort need is
    -- projected here so the two WHERE clauses below stay identical -- if they
    -- diverge, the total stops describing the page.
    ;WITH Jobs AS
    (
        SELECT
            e.[BookingType],
            e.[BookingId],
            [JobNumber]        = COALESCE(b.[JobNumber], n.[JobNumber]),
            [PayoutId]         = COALESCE(b.[PayoutId], n.[PayoutId]),
            [PayoutStatus]     = COALESCE(b.[PayoutStatus], n.[PayoutStatus]),
            e.[Status],
            e.[IsEarned],
            e.[IsPaid],
            e.[IsPrivate],
            e.[Amount],
            e.[Fee],
            e.[PaidAtUtc],
            e.[PaymentMethod],
            e.[ServiceId],
            [ServiceType]      = ps.[ServiceType],
            [ServiceCategory]  = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
            [SubCategory]      = COALESCE(b.[SubCategory], n.[SubCategory]),
            [ServiceItemCode]  = b.[ServiceItemCode],
            -- The day the job STARTS -- see the header on why this is not
            -- [BookingAmounts].[ServiceDate].
            [JobDate]          = COALESCE(b.[BookingDate], n.[CheckInDate]),
            -- Night-stay only; NULL on a single-day booking.
            [CheckOutDate]     = n.[CheckOutDate],
            [Nights]           = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                      THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
            -- A stay has no booked time window; its drop-off / pick-up stand in,
            -- which is what the agenda card shows for it.
            [StartTime]        = COALESCE(b.[StartTime], n.[DropOffTime]),
            [EndTime]          = COALESCE(b.[EndTime], n.[PickUpTime]),
            [LocationType]     = COALESCE(b.[LocationType], n.[LocationType]),
            [AddressLine]      = COALESCE(b.[SnapshotAddressLine], n.[SnapshotAddressLine]),
            [City]             = COALESCE(b.[SnapshotCity], n.[SnapshotCity]),
            [ZipCode]          = COALESCE(b.[SnapshotZipCode], n.[SnapshotZipCode]),
            [JobNotes]         = COALESCE(b.[JobNotes], n.[JobNotes]),
            e.[PetParentId],
            [CustomerName]     = COALESCE(pp.[FirstName] + N' ' + pp.[LastName], b.[CustomerName]),
            [CustomerPhotoUrl] = pp.[ProfilePhotoUrl],
            e.[PetId],
            [PetName]          = COALESCE(pet.[PetName], b.[PetName]),
            [AnimalType]       = COALESCE(pet.[PetType], b.[AnimalType]),
            [Breed]            = pet.[Breed],
            [PetGender]        = pet.[Gender],
            [CreatedAtUtc]     = COALESCE(b.[CreatedAtUtc], n.[CreatedAtUtc])
        FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
        LEFT JOIN [Booking].[Bookings] b
            ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
        LEFT JOIN [Booking].[NightStayBookings] n
            ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
        LEFT JOIN [Provider].[ProviderServices] ps
            ON ps.[ServiceId] = e.[ServiceId]
        LEFT JOIN [Parent].[PetParents] pp
            ON pp.[PetParentId] = e.[PetParentId]
        LEFT JOIN [Parent].[Pets] pet
            ON pet.[PetId] = e.[PetId]
    ),
    Filtered AS
    (
        SELECT j.*
        FROM Jobs j
        WHERE (@FilterByStatus = 0
               OR j.[Status] IN (SELECT [Value] FROM @StatusFilter))
          AND (@FilterByServiceType = 0
               OR j.[ServiceType] IN (SELECT [Value] FROM @ServiceTypeFilter))
          AND (@FilterByAnimalType = 0
               OR j.[AnimalType] IN (SELECT [Value] FROM @AnimalTypeFilter))
          AND (@ServiceId IS NULL OR j.[ServiceId] = @ServiceId)
          AND (@LocationType IS NULL OR j.[LocationType] = @LocationType)
          AND (@BreedPattern IS NULL
               OR LOWER(j.[Breed]) LIKE @BreedPattern ESCAPE N'\')
          AND (@MinEarnings IS NULL OR j.[Amount] >= @MinEarnings)
          AND (@MaxEarnings IS NULL OR j.[Amount] <= @MaxEarnings)
          -- Date overlap. A single-day booking collapses to
          -- [JobDate] BETWEEN @FromDate AND @ToDate, since its CheckOutDate is
          -- NULL and COALESCE falls back to the same day.
          AND (@FromDate IS NULL
               OR COALESCE(j.[CheckOutDate], j.[JobDate]) >= @FromDate)
          AND (@ToDate IS NULL OR j.[JobDate] <= @ToDate)
    )
    SELECT [TotalCount] = COUNT(*) FROM Filtered;

    -- Repeat the CTE for the page. Two statements rather than one because a CTE
    -- cannot be referenced across them; the WHERE is identical by construction.
    ;WITH Jobs AS
    (
        SELECT
            e.[BookingType],
            e.[BookingId],
            [JobNumber]        = COALESCE(b.[JobNumber], n.[JobNumber]),
            [PayoutId]         = COALESCE(b.[PayoutId], n.[PayoutId]),
            [PayoutStatus]     = COALESCE(b.[PayoutStatus], n.[PayoutStatus]),
            e.[Status],
            e.[IsEarned],
            e.[IsPaid],
            e.[IsPrivate],
            e.[Amount],
            e.[Fee],
            e.[PaidAtUtc],
            e.[PaymentMethod],
            e.[ServiceId],
            [ServiceType]      = ps.[ServiceType],
            [ServiceCategory]  = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
            [SubCategory]      = COALESCE(b.[SubCategory], n.[SubCategory]),
            [ServiceItemCode]  = b.[ServiceItemCode],
            [JobDate]          = COALESCE(b.[BookingDate], n.[CheckInDate]),
            [CheckOutDate]     = n.[CheckOutDate],
            [Nights]           = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                      THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
            [StartTime]        = COALESCE(b.[StartTime], n.[DropOffTime]),
            [EndTime]          = COALESCE(b.[EndTime], n.[PickUpTime]),
            [LocationType]     = COALESCE(b.[LocationType], n.[LocationType]),
            [AddressLine]      = COALESCE(b.[SnapshotAddressLine], n.[SnapshotAddressLine]),
            [City]             = COALESCE(b.[SnapshotCity], n.[SnapshotCity]),
            [ZipCode]          = COALESCE(b.[SnapshotZipCode], n.[SnapshotZipCode]),
            [JobNotes]         = COALESCE(b.[JobNotes], n.[JobNotes]),
            e.[PetParentId],
            [CustomerName]     = COALESCE(pp.[FirstName] + N' ' + pp.[LastName], b.[CustomerName]),
            [CustomerPhotoUrl] = pp.[ProfilePhotoUrl],
            e.[PetId],
            [PetName]          = COALESCE(pet.[PetName], b.[PetName]),
            [AnimalType]       = COALESCE(pet.[PetType], b.[AnimalType]),
            [Breed]            = pet.[Breed],
            [PetGender]        = pet.[Gender],
            [CreatedAtUtc]     = COALESCE(b.[CreatedAtUtc], n.[CreatedAtUtc])
        FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
        LEFT JOIN [Booking].[Bookings] b
            ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
        LEFT JOIN [Booking].[NightStayBookings] n
            ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
        LEFT JOIN [Provider].[ProviderServices] ps
            ON ps.[ServiceId] = e.[ServiceId]
        LEFT JOIN [Parent].[PetParents] pp
            ON pp.[PetParentId] = e.[PetParentId]
        LEFT JOIN [Parent].[Pets] pet
            ON pet.[PetId] = e.[PetId]
    ),
    Filtered AS
    (
        SELECT j.*
        FROM Jobs j
        WHERE (@FilterByStatus = 0
               OR j.[Status] IN (SELECT [Value] FROM @StatusFilter))
          AND (@FilterByServiceType = 0
               OR j.[ServiceType] IN (SELECT [Value] FROM @ServiceTypeFilter))
          AND (@FilterByAnimalType = 0
               OR j.[AnimalType] IN (SELECT [Value] FROM @AnimalTypeFilter))
          AND (@ServiceId IS NULL OR j.[ServiceId] = @ServiceId)
          AND (@LocationType IS NULL OR j.[LocationType] = @LocationType)
          AND (@BreedPattern IS NULL
               OR LOWER(j.[Breed]) LIKE @BreedPattern ESCAPE N'\')
          AND (@MinEarnings IS NULL OR j.[Amount] >= @MinEarnings)
          AND (@MaxEarnings IS NULL OR j.[Amount] <= @MaxEarnings)
          AND (@FromDate IS NULL
               OR COALESCE(j.[CheckOutDate], j.[JobDate]) >= @FromDate)
          AND (@ToDate IS NULL OR j.[JobDate] <= @ToDate)
    )
    SELECT
        [BookingType],
        [BookingId],
        [JobNumber],
        [PayoutId],
        [PayoutStatus],
        [Status],
        [IsEarned],
        [IsPaid],
        [IsPrivate],
        [Amount],
        [Fee],
        [PaidAtUtc],
        [PaymentMethod],
        [ServiceId],
        [ServiceType],
        [ServiceCategory],
        [SubCategory],
        [ServiceItemCode],
        [JobDate],
        [CheckOutDate],
        [Nights],
        [StartTime],
        [EndTime],
        [LocationType],
        [AddressLine],
        [City],
        [ZipCode],
        [JobNotes],
        [PetParentId],
        [CustomerName],
        [CustomerPhotoUrl],
        [PetId],
        [PetName],
        [AnimalType],
        [Breed],
        [PetGender],
        [CreatedAtUtc]
    FROM Filtered
    ORDER BY
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Asc'  THEN [Amount] END ASC,
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Desc' THEN [Amount] END DESC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Asc'  THEN [JobDate] END ASC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Desc' THEN [JobDate] END DESC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Asc'  THEN [StartTime] END ASC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Desc' THEN [StartTime] END DESC,
        -- Deterministic tie-break. Without it rows sharing a date (or an amount)
        -- can come back in a different order between two calls, which makes
        -- OFFSET paging repeat or skip a job.
        [BookingId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

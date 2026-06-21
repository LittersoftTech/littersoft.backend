CREATE OR ALTER PROCEDURE [Event].[ListEvents]
    @EventCategory   NVARCHAR(64)  = NULL,
    @EventType       NVARCHAR(32)  = NULL,
    @StartDate       DATE          = NULL,
    @EndDate         DATE          = NULL,
    @IsChildFriendly BIT           = NULL,
    -- JSON array of amenity codes (e.g. N'["Restrooms","FreeParking"]').
    -- When supplied, only events that carry EVERY listed amenity are returned.
    @AmenitiesJson   NVARCHAR(MAX) = NULL,
    -- Optional free-text title search. When supplied, only events whose Title
    -- CONTAINS the term (case-insensitive) are returned.
    @Title           NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Build a case-insensitive "contains" LIKE pattern for the title search.
    -- LIKE metacharacters in the user term are escaped (ESCAPE N'\') so they
    -- match literally, and LOWER() on both sides makes the match
    -- case-insensitive regardless of the database/column collation. NULL/blank
    -- term means "no title filter".
    DECLARE @TitlePattern NVARCHAR(410) = NULL;
    IF (@Title IS NOT NULL AND LTRIM(RTRIM(@Title)) <> N'')
    BEGIN
        SET @TitlePattern = N'%' +
            REPLACE(REPLACE(REPLACE(REPLACE(
                LOWER(LTRIM(RTRIM(@Title))),
                N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';
    END

    -- Materialise the requested amenity filter into a table variable once so we
    -- can both count it and join on it without re-parsing the JSON.
    DECLARE @Amenities TABLE ([Amenity] NVARCHAR(64) NOT NULL PRIMARY KEY);

    IF (@AmenitiesJson IS NOT NULL AND LTRIM(RTRIM(@AmenitiesJson)) <> N'')
    BEGIN
        INSERT INTO @Amenities ([Amenity])
        SELECT DISTINCT [value]
        FROM OPENJSON(@AmenitiesJson)
        WHERE [value] IS NOT NULL AND LTRIM(RTRIM([value])) <> N'';
    END

    DECLARE @AmenityCount INT = (SELECT COUNT(*) FROM @Amenities);

    -- Result set 1: event rows
    ;WITH FilteredEvents AS
    (
        SELECT e.[EventId]
        FROM [Event].[Events] e
        WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
          AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
          AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
          AND (@TitlePattern IS NULL OR LOWER(e.[Title]) LIKE @TitlePattern ESCAPE N'\')
          -- Date-range filter: event's [StartDate, EndDate] must overlap the
          -- caller's [@StartDate, @EndDate]. Each bound is independently optional.
          AND (@StartDate IS NULL OR e.[EndDate]   >= @StartDate)
          AND (@EndDate   IS NULL OR e.[StartDate] <= @EndDate)
          AND (
                @AmenityCount = 0
                OR @AmenityCount = (
                    SELECT COUNT(DISTINCT a.[Amenity])
                    FROM [Event].[EventAmenities] a
                    INNER JOIN @Amenities f ON f.[Amenity] = a.[Amenity]
                    WHERE a.[EventId] = e.[EventId])
              )
    )
    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly],
           e.[Title], e.[Description], e.[BannerImageUrl], e.[EventType],
           e.[StartDate], e.[EndDate], e.[StartTime], e.[EndTime],
           e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount],
           e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings]
    FROM [Event].[Events] e
    INNER JOIN FilteredEvents f ON f.[EventId] = e.[EventId]
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC, e.[EventId] ASC;

    -- Result set 2: (EventId, Amenity) pairs for the events above.
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
      AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
      AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
      AND (@TitlePattern IS NULL OR LOWER(e.[Title]) LIKE @TitlePattern ESCAPE N'\')
      AND (@StartDate IS NULL OR e.[EndDate]   >= @StartDate)
      AND (@EndDate   IS NULL OR e.[StartDate] <= @EndDate)
      AND (
            @AmenityCount = 0
            OR @AmenityCount = (
                SELECT COUNT(DISTINCT a2.[Amenity])
                FROM [Event].[EventAmenities] a2
                INNER JOIN @Amenities ff ON ff.[Amenity] = a2.[Amenity]
                WHERE a2.[EventId] = e.[EventId])
          )
    ORDER BY a.[EventId], a.[Amenity];
END;

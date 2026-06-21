CREATE OR ALTER PROCEDURE [Event].[CreateEvent]
    @ProviderId UNIQUEIDENTIFIER,
    @EventCategory NVARCHAR(64),
    @IsChildFriendly BIT,
    @Title NVARCHAR(200),
    @Description NVARCHAR(MAX),
    @BannerImageUrl NVARCHAR(1000) = NULL,
    @EventType NVARCHAR(32),
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = N'NoRefund',
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51030, 'Provider profile was not found.', 1;
    END

    DECLARE @InsertedEventId TABLE (EventId UNIQUEIDENTIFIER);

    INSERT INTO [Event].[Events]
    (
        [ProviderId],
        [EventCategory],
        [IsChildFriendly],
        [Title],
        [Description],
        [BannerImageUrl],
        [EventType],
        [StartDate],
        [EndDate],
        [StartTime],
        [EndTime],
        [IsPaid],
        [Price],
        [CancellationPolicy]
    )
    OUTPUT inserted.[EventId] INTO @InsertedEventId
    VALUES
    (
        @ProviderId,
        @EventCategory,
        @IsChildFriendly,
        @Title,
        @Description,
        @BannerImageUrl,
        @EventType,
        @StartDate,
        @EndDate,
        @StartTime,
        @EndTime,
        @IsPaid,
        CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END,
        @CancellationPolicy
    );

    DECLARE @EventId UNIQUEIDENTIFIER = (SELECT TOP (1) [EventId] FROM @InsertedEventId);

    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    -- Result set 1: event row (both organiser columns surfaced; PetParentId is NULL for provider-created events)
    SELECT e.[EventId],
           e.[ProviderId],
           e.[PetParentId],
           e.[EventCategory],
           e.[IsChildFriendly],
           e.[Title],
           e.[Description],
           e.[BannerImageUrl],
           e.[EventType],
           e.[StartDate],
           e.[EndDate],
           e.[StartTime],
           e.[EndTime],
           e.[CreatedAtUtc],
           e.[UpdatedAtUtc],
           e.[ViewCount],
           e.[ShareCount],
           e.[InquiryCount],
           e.[IsPaid],
           e.[Price],
           e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    -- Result set 2: amenities (zero or more rows)
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    COMMIT TRANSACTION;
END;

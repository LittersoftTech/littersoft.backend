CREATE OR ALTER PROCEDURE [Event].[CreatePetParentEvent]
    @PetParentId UNIQUEIDENTIFIER,
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
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51207, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedEventId TABLE (EventId UNIQUEIDENTIFIER);

    INSERT INTO [Event].[Events]
    (
        [PetParentId],
        [EventCategory],
        [IsChildFriendly],
        [Title],
        [Description],
        [BannerImageUrl],
        [EventType],
        [StartDate],
        [EndDate],
        [StartTime],
        [EndTime]
    )
    OUTPUT inserted.[EventId] INTO @InsertedEventId
    VALUES
    (
        @PetParentId,
        @EventCategory,
        @IsChildFriendly,
        @Title,
        @Description,
        @BannerImageUrl,
        @EventType,
        @StartDate,
        @EndDate,
        @StartTime,
        @EndTime
    );

    DECLARE @EventId UNIQUEIDENTIFIER = (SELECT TOP (1) [EventId] FROM @InsertedEventId);

    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    -- Result set 1: event row (both organiser columns surfaced; one is NULL)
    SELECT [EventId],
           [ProviderId],
           [PetParentId],
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
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    -- Result set 2: amenities
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    COMMIT TRANSACTION;
END;

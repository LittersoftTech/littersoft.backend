CREATE OR ALTER PROCEDURE [Event].[GetEvent]
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: event row (zero or one)
    SELECT [EventId],
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
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    -- Result set 2: amenities
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];
END;

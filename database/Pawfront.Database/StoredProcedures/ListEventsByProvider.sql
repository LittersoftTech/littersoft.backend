CREATE OR ALTER PROCEDURE [Event].[ListEventsByProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: event rows (PetParentId is always NULL on rows
    -- returned here because the filter is on ProviderId — but included
    -- for column-shape consistency with GetEvent / ListEvents readers).
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
    WHERE [ProviderId] = @ProviderId
    ORDER BY [StartDate] DESC, [StartTime] DESC;

    -- Result set 2: amenities for this provider's events (EventId + Amenity)
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE e.[ProviderId] = @ProviderId
    ORDER BY a.[EventId], a.[Amenity];
END;

CREATE OR ALTER PROCEDURE [Event].[CountEventsByOrganizer]
    @ProviderId UNIQUEIDENTIFIER = NULL,
    @PetParentId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Total events created by a single organiser — provider-organised when
    -- @ProviderId is supplied, parent-organised when @PetParentId is supplied.
    -- Surfaced on the event-detail read as the organiser's event count.
    SELECT COUNT(*)
    FROM [Event].[Events]
    WHERE (@ProviderId IS NOT NULL AND [ProviderId] = @ProviderId)
       OR (@PetParentId IS NOT NULL AND [PetParentId] = @PetParentId);
END;

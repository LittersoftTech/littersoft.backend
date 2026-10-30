-- Called under the EventLifecycle transaction lock by both create/update paths.
CREATE OR ALTER PROCEDURE [Event].[AssertHostEventNoOverlap]
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @ProviderId UNIQUEIDENTIFIER = NULL,
    @PetParentId UNIQUEIDENTIFIER = NULL,
    @ExcludedEventId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT = 0
        THROW 51468, 'Event overlap validation requires a write transaction.', 1;

    DECLARE @NewStart DATETIME2(7) = [Event].[EventStartUtc](@StartDate, @StartTime),
            @NewEnd DATETIME2(7) = [Event].[EventStartUtc](@EndDate, @EndTime);
    IF @NewStart >= @NewEnd
        THROW 51468, 'Event end must be after its start.', 1;

    DECLARE @Conflict NVARCHAR(2048) = (
        SELECT TOP (1) e.EventId AS conflictingEventId, e.Title AS conflictingEventTitle
        FROM [Event].[Events] e
        WHERE ((@ProviderId IS NOT NULL AND e.ProviderId = @ProviderId)
            OR (@PetParentId IS NOT NULL AND e.PetParentId = @PetParentId))
          AND e.EventStatus <> N'Cancelled'
          AND (@ExcludedEventId IS NULL OR e.EventId <> @ExcludedEventId)
          AND @NewStart < [Event].[EventStartUtc](e.EndDate, e.EndTime)
          AND [Event].[EventStartUtc](e.StartDate, e.StartTime) < @NewEnd
        ORDER BY e.StartDate, e.StartTime, e.EventId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );
    IF NULLIF(@Conflict, N'') IS NOT NULL
        THROW 51471, @Conflict, 1;
END;

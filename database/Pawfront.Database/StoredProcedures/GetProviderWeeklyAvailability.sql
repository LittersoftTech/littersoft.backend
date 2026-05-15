CREATE OR ALTER PROCEDURE [Provider].[GetProviderWeeklyAvailability]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ProviderId],
           [DayOfWeek],
           [IsOpen],
           [StartTime],
           [EndTime],
           [BreakStartTime],
           [BreakEndTime],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [DayOfWeek];
END;

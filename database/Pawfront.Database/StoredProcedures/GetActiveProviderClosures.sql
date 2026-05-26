CREATE OR ALTER PROCEDURE [Provider].[GetActiveClosuresForDate]
    @ServiceId UNIQUEIDENTIFIER,
    @Date DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Used by the slot service and booking service to find any closure that
    -- touches a particular calendar day for a specific service. Closures are
    -- per-service: a DayCare closure does not block NightStay slots/bookings.
    -- NULL StartTime/EndTime = full-day.
    SELECT [ClosureId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason]
    FROM [Provider].[ProviderClosures]
    WHERE [ServiceId] = @ServiceId
      AND @Date BETWEEN [StartDate] AND [EndDate]
    ORDER BY [StartTime];
END;

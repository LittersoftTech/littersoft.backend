CREATE OR ALTER PROCEDURE [Provider].[GetActiveClosuresForDate]
    @ProviderId UNIQUEIDENTIFIER,
    @Date DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Used by the slot service and booking service to find any closure that
    -- touches a particular calendar day. NULL StartTime/EndTime = full-day.
    SELECT [ClosureId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason]
    FROM [Provider].[ProviderClosures]
    WHERE [ProviderId] = @ProviderId
      AND @Date BETWEEN [StartDate] AND [EndDate]
    ORDER BY [StartTime];
END;

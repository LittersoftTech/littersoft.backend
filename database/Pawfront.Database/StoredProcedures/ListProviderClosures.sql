CREATE OR ALTER PROCEDURE [Provider].[ListClosures]
    @ProviderId UNIQUEIDENTIFIER,
    @From DATE = NULL,
    @To DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Return closures whose [StartDate, EndDate] interval intersects [@From, @To].
    -- A NULL bound means "open-ended" on that side.
    SELECT [ClosureId], [ProviderId], [StartDate], [EndDate],
           [StartTime], [EndTime], [Reason], [CreatedAtUtc]
    FROM [Provider].[ProviderClosures]
    WHERE [ProviderId] = @ProviderId
      AND (@To   IS NULL OR [StartDate] <= @To)
      AND (@From IS NULL OR [EndDate]   >= @From)
    ORDER BY [StartDate], [StartTime];
END;

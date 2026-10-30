-- Weekly rows are local wall-clock values. UTC is the default for legacy rows.
-- Keep SQL lifecycle jobs on the same calendar as the API availability reader.
CREATE OR ALTER FUNCTION [Provider].[WorkingHoursAtUtc]
(
    @ProviderId UNIQUEIDENTIFIER,
    @AtUtc DATETIME2(7)
)
RETURNS TABLE
AS
RETURN
(
    SELECT w.[IsOpen],
           [OpensAtUtc] = CAST((
               DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00' AS TIME), w.[StartTime]), localDay.[Day])
               AT TIME ZONE w.[WindowsTimeZone]) AT TIME ZONE 'UTC' AS DATETIME2(7)),
           [ClosesAtUtc] = CAST((
               DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00' AS TIME), w.[EndTime]), localDay.[Day])
               AT TIME ZONE w.[WindowsTimeZone]) AT TIME ZONE 'UTC' AS DATETIME2(7))
    FROM [Provider].[ProviderWeeklyAvailability] w
    CROSS APPLY (SELECT CAST(CAST((@AtUtc AT TIME ZONE 'UTC') AT TIME ZONE w.[WindowsTimeZone] AS DATE) AS DATETIME2(7)) AS [Day]) localDay
    WHERE w.[ProviderId] = @ProviderId
      AND w.[DayOfWeek] = ((DATEDIFF(DAY, '19000107', localDay.[Day]) % 7 + 7) % 7)
);

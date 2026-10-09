CREATE OR ALTER PROCEDURE [Provider].[SaveProviderWeeklyAvailability]
    @ProviderId UNIQUEIDENTIFIER,
    @AvailabilityJson NVARCHAR(MAX),
    @TimeZone NVARCHAR(128) = N'UTC',
    @WindowsTimeZone NVARCHAR(128) = N'UTC'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51050, 'Provider profile was not found.', 1;
    END

    -- Replace all 7 day rows atomically.
    DELETE FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId;

    INSERT INTO [Provider].[ProviderWeeklyAvailability]
    (
        [ProviderId],
        [TimeZone],
        [WindowsTimeZone],
        [DayOfWeek],
        [IsOpen],
        [StartTime],
        [EndTime],
        [BreakStartTime],
        [BreakEndTime]
    )
    SELECT @ProviderId, @TimeZone, @WindowsTimeZone,
           CAST(JSON_VALUE([value], '$.dayOfWeek') AS TINYINT),
           CAST(JSON_VALUE([value], '$.isOpen') AS BIT),
           CAST(JSON_VALUE([value], '$.startTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.endTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.breakStartTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.breakEndTime') AS TIME(0))
    FROM OPENJSON(@AvailabilityJson);

    SELECT [ProviderId],
           [DayOfWeek],
           [IsOpen],
           [StartTime],
           [EndTime],
           [BreakStartTime],
           [BreakEndTime],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [TimeZone]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [DayOfWeek];

    COMMIT TRANSACTION;
END;

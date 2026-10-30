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

    -- Preserve accepted legacy bookings before replacing their schedule.
    UPDATE b
    SET [WorkingHoursOpensAtUtc] = w.[OpensAtUtc],
        [WorkingHoursClosesAtUtc] = w.[ClosesAtUtc]
    FROM [Booking].[Bookings] b
    CROSS APPLY [Booking].[AcceptedWorkingHours](b.[ProviderId], b.[BookingDate], b.[StartTime]) w
    WHERE b.[ProviderId] = @ProviderId AND b.[WorkingHoursClosesAtUtc] IS NULL AND b.[Source] <> N'Custom'
      AND (b.[Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                         N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                         N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED', N'JOB_INCOMPLETE')
           OR EXISTS (SELECT 1 FROM [Booking].[BookingStatusHistory] h
                      WHERE h.[BookingId] = b.[BookingId] AND h.[ToStatus] = N'CONFIRMED'));

    UPDATE b
    SET [WorkingHoursOpensAtUtc] = w.[OpensAtUtc],
        [WorkingHoursClosesAtUtc] = w.[ClosesAtUtc]
    FROM [Booking].[NightStayBookings] b
    CROSS APPLY [Booking].[AcceptedWorkingHours](b.[ProviderId], b.[CheckInDate], b.[DropOffTime]) w
    WHERE b.[ProviderId] = @ProviderId AND b.[WorkingHoursClosesAtUtc] IS NULL
      AND (b.[Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                         N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                         N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED', N'JOB_INCOMPLETE')
           OR EXISTS (SELECT 1 FROM [Booking].[NightStayBookingStatusHistory] h
                      WHERE h.[NightStayBookingId] = b.[NightStayBookingId] AND h.[ToStatus] = N'CONFIRMED'));

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

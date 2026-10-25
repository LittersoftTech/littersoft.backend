CREATE OR ALTER PROCEDURE [Event].[SettleEventAttendance]

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    DELETE w FROM [Event].[Waitlist] w JOIN [Event].[Events] e ON e.EventId=w.EventId
    WHERE [Event].[EventStartUtc](e.StartDate,e.StartTime)<=SYSUTCDATETIME();

    DECLARE @BookingId UNIQUEIDENTIFIER, @Outcome NVARCHAR(16),@EventId UNIQUEIDENTIFIER,
        @AccountId UNIQUEIDENTIFIER,@Audience NVARCHAR(16),@WasFlagged BIT,@Type NVARCHAR(64),
        @Key NVARCHAR(200),@Close DATETIME2(7),@End DATETIME2(7),@Created DATETIME2(7);
    DECLARE bookings CURSOR LOCAL FAST_FORWARD FOR
        SELECT b.BookingId FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
        WHERE e.EventStatus=N'Active' AND b.Status IN (N'Confirmed',N'Attended') AND b.ReliabilitySettled=0
          AND SYSUTCDATETIME()>=[Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00')
        ORDER BY e.EndDate,e.EndTime,b.BookingId;
    OPEN bookings; FETCH NEXT FROM bookings INTO @BookingId;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SELECT @EventId=b.EventId,@AccountId=b.BookerAccountId,@Audience=b.BookerType,@Created=b.CreatedAtUtc,
            @Close=[Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00'),
            @End=[Event].[EventStartUtc](e.EndDate,e.EndTime)
        FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId WHERE b.BookingId=@BookingId;
        SET @WasFlagged=CASE WHEN EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@AccountId AND RedFlagExpiresAt>SYSUTCDATETIME()) THEN 1 ELSE 0 END;
        UPDATE [Event].[EventBookingTickets] SET PaymentStatus=N'NoShow' WHERE BookingId=@BookingId AND PaymentStatus=N'Pending'
            AND EXISTS (SELECT 1 FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId WHERE b.BookingId=@BookingId AND e.IsPaid=1);
        SET @Outcome=CASE WHEN EXISTS (SELECT 1 FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND PaymentStatus=N'NoShow') THEN N'Negative'
            WHEN EXISTS (SELECT 1 FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId WHERE b.BookingId=@BookingId AND e.IsPaid=1) THEN N'PaidAttendance' ELSE N'Attendance' END;
        UPDATE [Event].[EventBookings] SET Status=CASE WHEN @Outcome=N'Negative' THEN N'NoShow' ELSE N'Attended' END,
            ReliabilitySettled=1,UpdatedAtUtc=SYSUTCDATETIME() WHERE BookingId=@BookingId;
        EXEC [Event].[RecordReliabilityOutcome] @BookingId,@Outcome;
        SET @Type=CASE WHEN @Outcome<>N'Negative' THEN N'EVENT_ATTENDED'
            WHEN @WasFlagged=0 AND EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@AccountId AND RedFlagExpiresAt>SYSUTCDATETIME())
            THEN N'EVENT_PROFILE_FLAGGED' ELSE N'EVENT_MISSED' END;
        IF EXISTS (SELECT 1 FROM [Notification].[EventNotificationDelivery] WHERE NotificationType=@Type
            AND (@Close>=ActivatedAtUtc OR @Created>=ActivatedAtUtc))
        BEGIN
            SET @Key=CONCAT(@Type,N':',@BookingId);
            IF @Type=N'EVENT_ATTENDED'
                EXEC [Event].[NotifyEventAccount] @EventId,@AccountId,@Audience,@Type,@Key,@BookingId,@DueAtUtc=@End;
            ELSE
                EXEC [Event].[NotifyEventAccount] @EventId,@AccountId,@Audience,@Type,@Key,@BookingId,@DueAtUtc=@Close;
        END
        FETCH NEXT FROM bookings INTO @BookingId;
    END
    CLOSE bookings; DEALLOCATE bookings;

    COMMIT TRANSACTION;
END;

CREATE OR ALTER PROCEDURE [Event].[SettleEventAttendance]

AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    DECLARE @BookingId UNIQUEIDENTIFIER, @Outcome NVARCHAR(16);
    DECLARE bookings CURSOR LOCAL FAST_FORWARD FOR
        SELECT b.BookingId FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
        WHERE e.EventStatus=N'Active' AND b.Status IN (N'Confirmed',N'Attended') AND b.ReliabilitySettled=0
          AND SYSUTCDATETIME()>=[Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00')
        ORDER BY e.EndDate,e.EndTime,b.BookingId;
    OPEN bookings; FETCH NEXT FROM bookings INTO @BookingId;
    WHILE @@FETCH_STATUS=0
    BEGIN
        UPDATE [Event].[EventBookingTickets] SET PaymentStatus=N'NoShow' WHERE BookingId=@BookingId AND PaymentStatus=N'Pending';
        SET @Outcome=CASE WHEN EXISTS (SELECT 1 FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND PaymentStatus=N'NoShow') THEN N'Negative'
            WHEN EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE BookingId=@BookingId AND TotalAmount>0) THEN N'PaidAttendance' ELSE N'Attendance' END;
        UPDATE [Event].[EventBookings] SET Status=CASE WHEN @Outcome=N'Negative' THEN N'NoShow' ELSE N'Attended' END,
            ReliabilitySettled=1,UpdatedAtUtc=SYSUTCDATETIME() WHERE BookingId=@BookingId;
        EXEC [Event].[RecordReliabilityOutcome] @BookingId,@Outcome;
        FETCH NEXT FROM bookings INTO @BookingId;
    END
    CLOSE bookings; DEALLOCATE bookings;

    COMMIT TRANSACTION;
END;

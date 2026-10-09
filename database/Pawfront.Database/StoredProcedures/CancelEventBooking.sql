CREATE OR ALTER PROCEDURE [Event].[CancelEventBooking]
 @BookingId UNIQUEIDENTIFIER, @BookerEmail NVARCHAR(320)
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

 DECLARE @Status NVARCHAR(32), @EventId UNIQUEIDENTIFIER, @Name NVARCHAR(200), @Count INT;
 SELECT @Status=Status,@EventId=EventId,@Name=BookerName,@Count=TicketCount FROM [Event].[EventBookings]
 WHERE BookingId=@BookingId AND BookerEmail=@BookerEmail;
 IF @Status IS NULL THROW 51218, 'Event booking was not found for this booker.', 1;
 IF @Status IN (N'Cancelled',N'CancelledByParent',N'EventCancelled') THROW 51219, 'Event booking is already cancelled.', 1;
 IF @Status<>N'Confirmed' OR EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND [Event].[EventStartUtc](StartDate,StartTime)<=SYSUTCDATETIME())
     THROW 51468, 'Only bookings for events that have not started can be cancelled.', 1;
 DECLARE @Hours INT,@Start DATETIME2(7);
 SELECT @Hours=CASE CancellationPolicy WHEN N'FullRefundUpTo48Hours' THEN 48 WHEN N'FullRefundUpTo24Hours' THEN 24
     WHEN N'FullRefundUpTo4Hours' THEN 4 WHEN N'FullRefundUpTo2Hours' THEN 2 ELSE 0 END,
     @Start=[Event].[EventStartUtc](StartDate,StartTime) FROM [Event].[Events] WHERE EventId=@EventId;
 UPDATE [Event].[EventBookings] SET Status=N'CancelledByParent',CancelledAtUtc=SYSUTCDATETIME(),UpdatedAtUtc=SYSUTCDATETIME()
 WHERE BookingId=@BookingId;
 IF @Hours>0 AND SYSUTCDATETIME()>DATEADD(hour,-@Hours,@Start)
     EXEC [Event].[RecordReliabilityOutcome] @BookingId,N'Negative';
 EXEC [Notification].[EnqueueEventNotification] @EventId=@EventId,@EventBookingId=@BookingId,
     @NotificationType=N'EVENT_TICKET_CANCELLED',@BookerName=@Name,@TicketCount=@Count;
 EXEC [Event].[NotifyEventWaitlist] @EventId;
 COMMIT TRANSACTION;
 EXEC [Event].[GetEventBooking] @BookingId;
END;

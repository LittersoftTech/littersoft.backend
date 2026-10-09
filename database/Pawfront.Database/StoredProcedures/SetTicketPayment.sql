CREATE OR ALTER PROCEDURE [Event].[SetTicketPayment]
    @BookingId UNIQUEIDENTIFIER, @TicketNumber INT, @PetParentId UNIQUEIDENTIFIER,
    @PaymentStatus NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    IF @PaymentStatus NOT IN (N'Pending',N'CashReceived') OR @PaymentStatus IS NULL
        THROW 51468, 'paymentStatus must be Pending or CashReceived.', 1;
    DECLARE @StartDate DATE,@EndDate DATE,@Status NVARCHAR(32),@Settled BIT;
    SELECT @StartDate=e.StartDate,@EndDate=e.EndDate,@Status=b.Status,@Settled=b.ReliabilitySettled
    FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
    JOIN [Event].[EventBookingTickets] t ON t.BookingId=b.BookingId AND t.TicketNumber=@TicketNumber
    WHERE b.BookingId=@BookingId AND e.PetParentId=@PetParentId AND e.EventStatus=N'Active';
    IF @EndDate IS NULL THROW 51460, 'Ticket was not found for this host.', 1;
    IF SYSUTCDATETIME()<[Event].[EventStartUtc](@StartDate,'00:00') OR SYSUTCDATETIME()>=[Event].[EventStartUtc](DATEADD(day,1,@EndDate),'00:00') OR @Settled=1
        THROW 51467, 'Attendance can only be recorded on the event days before midnight.', 1;
    IF @Status NOT IN (N'Confirmed',N'Attended') THROW 51468, 'This booking cannot record attendance.', 1;
    UPDATE [Event].[EventBookingTickets] SET PaymentStatus=@PaymentStatus WHERE BookingId=@BookingId AND TicketNumber=@TicketNumber;
    UPDATE [Event].[EventBookings] SET UpdatedAtUtc=SYSUTCDATETIME(),
        Status=CASE WHEN NOT EXISTS (SELECT 1 FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND PaymentStatus<>N'CashReceived') THEN N'Attended' ELSE N'Confirmed' END
    WHERE BookingId=@BookingId;

    COMMIT TRANSACTION;
END;

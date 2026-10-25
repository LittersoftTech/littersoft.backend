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
    IF EXISTS (SELECT 1 FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId WHERE b.BookingId=@BookingId AND e.IsPaid=0)
        THROW 51468, 'Free events do not record attendance or cash.', 1;
    IF SYSUTCDATETIME()<[Event].[EventStartUtc](@StartDate,'00:00') OR SYSUTCDATETIME()>=[Event].[EventStartUtc](DATEADD(day,1,@EndDate),'00:00') OR @Settled=1
        THROW 51467, 'Attendance can only be recorded on the event days before midnight.', 1;
    IF @Status NOT IN (N'Confirmed',N'Attended') THROW 51468, 'This booking cannot record attendance.', 1;
    DECLARE @PreviousPaymentStatus NVARCHAR(16)=(SELECT PaymentStatus FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND TicketNumber=@TicketNumber);
    DECLARE @WasFullyPaid BIT=CASE WHEN NOT EXISTS (SELECT 1 FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND PaymentStatus<>N'CashReceived') THEN 1 ELSE 0 END;
    UPDATE [Event].[EventBookingTickets] SET PaymentStatus=@PaymentStatus WHERE BookingId=@BookingId AND TicketNumber=@TicketNumber;
    UPDATE [Event].[EventBookings] SET UpdatedAtUtc=SYSUTCDATETIME(),
        Status=N'Confirmed'
    WHERE BookingId=@BookingId;

    DECLARE @IsFullyPaid BIT=CASE WHEN NOT EXISTS (SELECT 1 FROM [Event].[EventBookingTickets] WHERE BookingId=@BookingId AND PaymentStatus<>N'CashReceived') THEN 1 ELSE 0 END;
    IF @PreviousPaymentStatus<>@PaymentStatus AND @WasFullyPaid<>@IsFullyPaid
    BEGIN
        DECLARE @EventId UNIQUEIDENTIFIER,@AccountId UNIQUEIDENTIFIER,@Audience NVARCHAR(16),
            @EventTitle NVARCHAR(200),@HostName NVARCHAR(201),@Amount NVARCHAR(40),@Data NVARCHAR(MAX),@Type NVARCHAR(64);
        SELECT @EventId=b.EventId,@AccountId=b.BookerAccountId,@Audience=b.BookerType,
            @EventTitle=e.Title,@HostName=LTRIM(RTRIM(CONCAT(p.FirstName,N' ',p.LastName))),
            @Amount=FORMAT(b.TotalAmount,'0.##','en-US')
        FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
        JOIN [Parent].[PetParents] p ON p.PetParentId=e.PetParentId WHERE b.BookingId=@BookingId;
        SET @Type=CASE WHEN @PaymentStatus=N'CashReceived' THEN N'EVENT_BOOKING_CASH_RECEIVED' ELSE N'EVENT_BOOKING_PAYMENT_PENDING' END;
        SET @Data=(SELECT @Type AS type,N'EVENT' AS category,LOWER(CONVERT(NVARCHAR(36),@EventId)) AS eventId,
            LOWER(CONVERT(NVARCHAR(36),@BookingId)) AS bookingId,LOWER(CONVERT(NVARCHAR(36),@BookingId)) AS entityId,
            @Amount AS amount,@EventTitle AS eventTitle,@HostName AS hostName FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        EXEC [Event].[NotifyEventAccount] @EventId,@AccountId,@Audience,@Type,NULL,@BookingId,@Data;
    END

    COMMIT TRANSACTION;
END;

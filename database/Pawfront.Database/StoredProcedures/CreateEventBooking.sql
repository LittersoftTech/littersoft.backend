-- Inserts a single event booking + N child ticket rows in one transaction.
--
-- Capacity is enforced by SUMming TicketCount across confirmed bookings for
-- the same EventId under UPDLOCK + HOLDLOCK, so concurrent buyers serialise
-- and the (N+1)-th seat is rejected once @MaximumCapacity is reached. The
-- maximum capacity is fetched from the Cosmos doc by the caller and passed
-- in here — Cosmos is the source of truth, this sproc just gates against it.
--
-- Bookings are created with PaymentStatus = N'Pending'. The external gateway
-- callback ([Event].[ConfirmEventBookingPayment]) flips it to N'Paid'.
-- Status = N'Confirmed' from the start because the seat is held; refunds /
-- cancellations would flip Status to N'Cancelled' and free the capacity.
CREATE OR ALTER PROCEDURE [Event].[CreateEventBooking]
    @EventId UNIQUEIDENTIFIER,
    @BookerName NVARCHAR(200),
    @BookerEmail NVARCHAR(320),
    @BookerMobile NVARCHAR(32) = NULL,
    @PaymentMethod NVARCHAR(32),
    -- NULL for online events: they have no venue capacity, so the capacity
    -- check below is skipped and any number of bookings is accepted (each
    -- online booking is capped to one ticket by the application layer).
    @MaximumCapacity INT = NULL,
    @TotalAmount DECIMAL(18, 2),
    @AttendeeNames [Event].[EventBookingAttendeeNames] READONLY,
    @BookerAccountId UNIQUEIDENTIFIER=NULL, @BookerType NVARCHAR(16)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TicketCount INT = (SELECT COUNT(*) FROM @AttendeeNames);

    IF @TicketCount < 1
    BEGIN
        THROW 51094, 'At least one attendee name is required.', 1;
    END

    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;


    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId
    )
    BEGIN
        THROW 51090, 'Event was not found.', 1;
    END


    IF @BookerAccountId IS NULL OR @BookerType NOT IN (N'PetParent',N'Provider')
        THROW 51468, 'An authenticated account profile is required to book events.', 1;
    IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND EventStatus=N'Active')
        THROW 51461, 'Cancelled events cannot be booked.', 1;
    IF EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId
        AND SYSUTCDATETIME() >= DATEADD(hour,-1,[Event].[EventStartUtc](StartDate,StartTime)))
        THROW 51470, 'Ticket bookings for this event are closed.', 1;
    IF EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE EventId=@EventId AND BookerAccountId=@BookerAccountId AND Status IN (N'Confirmed',N'Attended'))
        THROW 51469, 'You already hold a booking for this event.', 1;
    DECLARE @ActiveCount INT=(SELECT COUNT(*) FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
        WHERE b.BookerAccountId=@BookerAccountId AND b.Status=N'Confirmed' AND e.EventStatus=N'Active'
        AND [Event].[EventStartUtc](e.StartDate,e.StartTime)>SYSUTCDATETIME());
    DECLARE @HeldCount INT=(SELECT COUNT(*) FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
        WHERE b.BookerAccountId=@BookerAccountId AND b.Status IN (N'Confirmed',N'Attended') AND e.EventStatus=N'Active'
          AND [Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00')>SYSUTCDATETIME());
    IF @HeldCount>=1 AND EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@BookerAccountId AND RedFlagExpiresAt>SYSUTCDATETIME())
        THROW 51463, 'Red-flagged accounts may hold only one upcoming booking.', 1;
    IF @ActiveCount>=5 THROW 51462, 'An account may hold at most five upcoming event bookings.', 1;
    SELECT @MaximumCapacity=CASE WHEN EventType=N'Online' THEN NULL ELSE COALESCE(MaximumCapacity,@MaximumCapacity) END,
        @TotalAmount=CASE WHEN IsPaid=1 THEN ISNULL(Price,0)*@TicketCount ELSE 0 END
    FROM [Event].[Events] WHERE EventId=@EventId;

    -- Race-safe capacity check. UPDLOCK + HOLDLOCK on the SUM forces
    -- concurrent CreateEventBooking calls for the same event to serialise,
    -- so two buyers can't both claim the last seat.
    DECLARE @ReservedTickets INT;
    SELECT @ReservedTickets = ISNULL(SUM([TicketCount]), 0)
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [EventId] = @EventId
      AND [Status] IN (N'Confirmed',N'Attended');

    IF @MaximumCapacity IS NOT NULL AND @ReservedTickets + @TicketCount > @MaximumCapacity
    BEGIN
        THROW 51091, 'Event is sold out or does not have enough remaining capacity.', 1;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Event].[EventBookings]
    (
        [EventId],
        [BookerAccountId], [BookerType],
        [BookerName],
        [BookerEmail],
        [BookerMobile],
        [TicketCount],
        [PaymentMethod],
        [TotalAmount]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @EventId,
        @BookerAccountId, @BookerType,
        @BookerName,
        @BookerEmail,
        @BookerMobile,
        @TicketCount,
        @PaymentMethod,
        @TotalAmount
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    INSERT INTO [Event].[EventBookingTickets]
        ([BookingId], [EventId], [TicketNumber], [AttendeeName])
    SELECT @BookingId, @EventId, a.[TicketNumber], a.[AttendeeName]
    FROM @AttendeeNames AS a;

    -- Tell the ORGANISER somebody bought tickets. No self-notification risk: an
    -- organiser is blocked from booking their own event (403 SelfBookingNotAllowed),
    -- so the buyer is always someone else. The buyer gets nothing here — buying is
    -- their own action, and they saw the confirmation on screen.
    EXEC [Notification].[EnqueueEventNotification]
        @EventId = @EventId,
        @EventBookingId = @BookingId,
        @NotificationType = N'EVENT_TICKET_SOLD',
        @BookerName = @BookerName,
        @TicketCount = @TicketCount;

    DELETE FROM [Event].[Waitlist] WHERE EventId=@EventId AND PetParentId=@BookerAccountId;
    COMMIT TRANSACTION;
    EXEC [Event].[GetEventBooking] @BookingId;
END;

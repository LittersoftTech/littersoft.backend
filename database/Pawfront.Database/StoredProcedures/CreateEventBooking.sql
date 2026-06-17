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
    @AttendeeNames [Event].[EventBookingAttendeeNames] READONLY
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

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId
    )
    BEGIN
        THROW 51090, 'Event was not found.', 1;
    END

    -- Race-safe capacity check. UPDLOCK + HOLDLOCK on the SUM forces
    -- concurrent CreateEventBooking calls for the same event to serialise,
    -- so two buyers can't both claim the last seat.
    DECLARE @ReservedTickets INT;
    SELECT @ReservedTickets = ISNULL(SUM([TicketCount]), 0)
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [EventId] = @EventId
      AND [Status] = N'Confirmed';

    IF @MaximumCapacity IS NOT NULL AND @ReservedTickets + @TicketCount > @MaximumCapacity
    BEGIN
        THROW 51091, 'Event is sold out or does not have enough remaining capacity.', 1;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Event].[EventBookings]
    (
        [EventId],
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

    -- Result set 1: the booking row.
    SELECT [BookingId],
           [EventId],
           [BookerName],
           [BookerEmail],
           [BookerMobile],
           [TicketCount],
           [PaymentMethod],
           [PaymentStatus],
           [PaymentReference],
           [TotalAmount],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    -- Result set 2: the ticket rows (ordered by TicketNumber).
    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc]
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
    ORDER BY [TicketNumber];

    COMMIT TRANSACTION;
END;

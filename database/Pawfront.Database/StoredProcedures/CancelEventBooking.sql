-- Soft-cancels an event ticket booking on behalf of the booker. Ownership is
-- proven by matching @BookerEmail (the caller's Firebase email claim) against
-- the booking's free-text BookerEmail — there is no FK to any user table.
--
-- The row is kept (payment/history preserved); flipping Status to N'Cancelled'
-- releases the seat capacity automatically, because [Event].[CreateEventBooking]
-- only SUMs TicketCount over rows where Status = N'Confirmed'. The UPDLOCK +
-- HOLDLOCK on the row read serialises against a concurrent double-cancel and
-- against the capacity check in CreateEventBooking.
--
-- THROW 51218 — booking not found for this booker (unknown id OR not theirs).
-- THROW 51219 — booking already cancelled.
CREATE OR ALTER PROCEDURE [Event].[CancelEventBooking]
    @BookingId UNIQUEIDENTIFIER,
    @BookerEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @EventId UNIQUEIDENTIFIER;
    DECLARE @BookerName NVARCHAR(200);
    DECLARE @TicketCount INT;

    SELECT @CurrentStatus = [Status],
           @EventId = [EventId],
           @BookerName = [BookerName],
           @TicketCount = [TicketCount]
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [BookerEmail] = @BookerEmail;

    IF @CurrentStatus IS NULL
        THROW 51218, 'Event booking was not found for this booker.', 1;

    IF @CurrentStatus = N'Cancelled'
        THROW 51219, 'Event booking is already cancelled.', 1;

    UPDATE [Event].[EventBookings]
    SET [Status] = N'Cancelled',
        [CancelledAtUtc] = SYSUTCDATETIME(),
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [BookingId] = @BookingId;

    -- Tell the ORGANISER their seats are free again — they need to know the
    -- attendee list changed, and they didn't perform this action. The booker gets
    -- nothing: cancelling is their own tap.
    EXEC [Notification].[EnqueueEventNotification]
        @EventId = @EventId,
        @EventBookingId = @BookingId,
        @NotificationType = N'EVENT_TICKET_CANCELLED',
        @BookerName = @BookerName,
        @TicketCount = @TicketCount;

    -- Result set 1: the cancelled booking row (same shape as GetEventBooking RS1).
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

    -- Result set 2: tickets (zero or more, ordered by TicketNumber) so the
    -- response carries the full shape the client got on create / GET.
    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc]
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
    ORDER BY [TicketNumber];
END;

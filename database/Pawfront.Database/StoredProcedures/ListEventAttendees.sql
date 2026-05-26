-- Organiser-scoped attendee list. Joins booking + ticket rows for one
-- event, restricted to bookings the organiser actually owns (the event
-- must belong to @ProviderId). Throws 51095 if the event is unknown or
-- owned by someone else.
--
-- Includes all non-cancelled bookings — Pending/Failed payments are
-- surfaced with their PaymentStatus so the organiser can chase them.
CREATE OR ALTER PROCEDURE [Event].[ListEventAttendees]
    @ProviderId UNIQUEIDENTIFIER,
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51095, 'Event was not found.', 1;
    END

    SELECT t.[TicketId],
           t.[BookingId],
           t.[TicketNumber],
           t.[AttendeeName],
           b.[BookerName],
           b.[BookerEmail],
           b.[BookerMobile],
           b.[PaymentMethod],
           b.[PaymentStatus],
           b.[TotalAmount],
           t.[CreatedAtUtc]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;
END;

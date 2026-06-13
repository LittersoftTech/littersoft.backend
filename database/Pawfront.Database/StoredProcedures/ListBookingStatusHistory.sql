-- Returns the full status-change audit trail for a booking, oldest-first (the
-- seeded creation entry sorts first). Empty when the booking has no history or
-- doesn't exist — the API returns [] rather than 404. Authorization (the caller
-- is a party to the booking) is enforced at the endpoint layer.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingStatusHistory]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingStatusHistoryId],
           [BookingId],
           [FromStatus],
           [ToStatus],
           [ChangedByActor],
           [ChangedByActorId],
           [Note],
           [ChangedAtUtc]
    FROM [Booking].[BookingStatusHistory]
    WHERE [BookingId] = @BookingId
    ORDER BY [ChangedAtUtc] ASC, [BookingStatusHistoryId] ASC;
END;

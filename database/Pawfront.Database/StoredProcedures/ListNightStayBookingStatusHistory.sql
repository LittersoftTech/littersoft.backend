-- Returns the full status-change audit trail for a night-stay booking,
-- oldest-first (the seeded creation entry sorts first). Empty when the booking
-- has no history or doesn't exist. Authorization (the caller is a party to the
-- booking) is enforced at the endpoint layer.
CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingStatusHistory]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingStatusHistoryId],
           [NightStayBookingId],
           [FromStatus],
           [ToStatus],
           [ChangedByActor],
           [ChangedByActorId],
           [Note],
           [ChangedAtUtc]
    FROM [Booking].[NightStayBookingStatusHistory]
    WHERE [NightStayBookingId] = @NightStayBookingId
    ORDER BY [ChangedAtUtc] ASC, [NightStayBookingStatusHistoryId] ASC;
END;

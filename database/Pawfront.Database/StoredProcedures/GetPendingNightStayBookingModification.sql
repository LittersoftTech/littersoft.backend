-- Reads the staged (pending) check-in/check-out change proposal for a multi-night
-- booking. Empty result when there is no open proposal (at most one per booking).
CREATE OR ALTER PROCEDURE [Booking].[GetPendingNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingModificationId] AS [BookingModificationId], [NightStayBookingId] AS [BookingId],
           [RequestedByActor], [RequestedByActorId],
           [ProposedCheckInDate], [ProposedCheckOutDate], [RequestNote], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingModifications]
    WHERE [NightStayBookingId] = @NightStayBookingId;
END;

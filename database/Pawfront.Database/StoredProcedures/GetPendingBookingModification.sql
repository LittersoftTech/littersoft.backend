-- Reads the staged (pending) date/time-change proposal for a single-day booking,
-- so the counterparty can see what's proposed before accepting/declining. Empty
-- result when there is no open proposal (at most one per booking).
CREATE OR ALTER PROCEDURE [Booking].[GetPendingBookingModification]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingModificationId], [BookingId], [RequestedByActor], [RequestedByActorId],
           [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote], [CreatedAtUtc]
    FROM [Booking].[BookingModifications]
    WHERE [BookingId] = @BookingId;
END;

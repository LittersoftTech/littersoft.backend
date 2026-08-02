-- Reads the staged (pending) date/time-change proposal for a single-day booking,
-- so the counterparty can see what's proposed before accepting/declining. Empty
-- result when there is no open proposal (at most one per booking). Also returns
-- the acknowledged terms staged with the proposal (when the provider's terms had
-- drifted), so the responder sees that accepting re-prices / re-rules the booking.
CREATE OR ALTER PROCEDURE [Booking].[GetPendingBookingModification]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingModificationId], [BookingId], [RequestedByActor], [RequestedByActorId],
           [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote], [CreatedAtUtc],
           [HasAcknowledgedTerms], [AcknowledgedPricePerHour], [AcknowledgedCancellationPolicyHours],
           [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
           [AcknowledgedLatitude], [AcknowledgedLongitude]
    FROM [Booking].[BookingModifications]
    WHERE [BookingId] = @BookingId;
END;

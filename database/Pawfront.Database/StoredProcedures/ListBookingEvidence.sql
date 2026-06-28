-- Lists the job-completion evidence photos for a single-day booking, oldest
-- first. Empty result when none (list semantics, no THROW).
CREATE OR ALTER PROCEDURE [Booking].[ListBookingEvidence]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingEvidenceId], [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[BookingEvidence]
    WHERE [BookingId] = @BookingId
    ORDER BY [CreatedAtUtc] ASC;
END;

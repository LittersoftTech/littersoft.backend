-- Lists the job-completion evidence photos for a multi-night booking, oldest
-- first. Empty result when none.
CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingEvidence]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingEvidenceId] AS [BookingEvidenceId], [NightStayBookingId] AS [BookingId],
           [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingEvidence]
    WHERE [NightStayBookingId] = @NightStayBookingId
    ORDER BY [CreatedAtUtc] ASC;
END;

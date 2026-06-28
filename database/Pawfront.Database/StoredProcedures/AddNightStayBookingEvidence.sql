-- Records one job-completion evidence photo for a multi-night booking. Mirror of
-- [Booking].[AddBookingEvidence]. THROW 51270 booking not found / not owned.
CREATE OR ALTER PROCEDURE [Booking].[AddNightStayBookingEvidence]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @NightStayBookingId AND [ProviderId] = @ProviderId)
    BEGIN
        THROW 51270, 'Night stay booking was not found for this provider.', 1;
    END

    DECLARE @Inserted TABLE ([NightStayBookingEvidenceId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingEvidence] ([NightStayBookingId], [PhotoUrl])
    OUTPUT inserted.[NightStayBookingEvidenceId] INTO @Inserted
    VALUES (@NightStayBookingId, @PhotoUrl);

    SELECT [NightStayBookingEvidenceId] AS [BookingEvidenceId], [NightStayBookingId] AS [BookingId],
           [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingEvidence]
    WHERE [NightStayBookingEvidenceId] = (SELECT TOP (1) [NightStayBookingEvidenceId] FROM @Inserted);
END;

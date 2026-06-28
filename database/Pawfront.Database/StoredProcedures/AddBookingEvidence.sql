-- Records one job-completion evidence photo for a single-day booking (the blob
-- upload happens in the app layer; this stores the resulting URL). Verifies the
-- booking exists and belongs to the provider. THROW 51150 booking not found /
-- not owned by this provider.
CREATE OR ALTER PROCEDURE [Booking].[AddBookingEvidence]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId AND [ProviderId] = @ProviderId)
    BEGIN
        THROW 51150, 'Booking was not found for this provider.', 1;
    END

    DECLARE @Inserted TABLE ([BookingEvidenceId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingEvidence] ([BookingId], [PhotoUrl])
    OUTPUT inserted.[BookingEvidenceId] INTO @Inserted
    VALUES (@BookingId, @PhotoUrl);

    SELECT [BookingEvidenceId], [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[BookingEvidence]
    WHERE [BookingEvidenceId] = (SELECT TOP (1) [BookingEvidenceId] FROM @Inserted);
END;

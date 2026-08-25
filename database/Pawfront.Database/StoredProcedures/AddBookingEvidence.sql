-- Records one job-completion evidence photo for a single-day booking (the blob
-- upload happens in the app layer; this stores the resulting URL). Verifies the
-- booking exists and belongs to the provider. THROW 51150 booking not found /
-- not owned by this provider.
--
-- The provider's geolocation at the moment the photo was taken is written here
-- too, in one transaction with the evidence row — a photo whose location was lost
-- is exactly the case the capture exists to prevent. One location row per photo,
-- which is why this trigger (unlike the others) legitimately repeats. The PARENT's
-- own fix for this moment arrives separately via
-- [Booking].[RecordBookingLocationEvent] with the same 'EvidenceCaptured' trigger.
CREATE OR ALTER PROCEDURE [Booking].[AddBookingEvidence]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
    -- See [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

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

    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'EvidenceCaptured', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [BookingEvidenceId], [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[BookingEvidence]
    WHERE [BookingEvidenceId] = (SELECT TOP (1) [BookingEvidenceId] FROM @Inserted);

    COMMIT TRANSACTION;
END;

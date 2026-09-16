-- Records one job-completion evidence photo for a multi-night booking. Mirror of
-- [Booking].[AddBookingEvidence]. THROW 51270 booking not found / not owned.
-- Also records the provider's geolocation for the photo, in one transaction with
-- the evidence row — one location per photo, so this trigger legitimately repeats.
CREATE OR ALTER PROCEDURE [Booking].[AddNightStayBookingEvidence]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
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
        SELECT 1 FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @NightStayBookingId AND [ProviderId] = @ProviderId)
    BEGIN
        THROW 51270, 'Night stay booking was not found for this provider.', 1;
    END

    DECLARE @Inserted TABLE ([NightStayBookingEvidenceId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingEvidence] ([NightStayBookingId], [PhotoUrl])
    OUTPUT inserted.[NightStayBookingEvidenceId] INTO @Inserted
    VALUES (@NightStayBookingId, @PhotoUrl);

    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'EvidenceCaptured', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [NightStayBookingEvidenceId] AS [BookingEvidenceId], [NightStayBookingId] AS [BookingId],
           [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingEvidence]
    WHERE [NightStayBookingEvidenceId] = (SELECT TOP (1) [NightStayBookingEvidenceId] FROM @Inserted);

    COMMIT TRANSACTION;
END;

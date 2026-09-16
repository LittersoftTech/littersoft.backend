-- Night-stay twin of [Booking].[RecordBookingLocationEvent] — see that file for
-- which moments arrive here rather than inside a transition sproc, and why.
-- Night stays are App-only, so [PetParentId] is NOT NULL and there is no Custom
-- walk-in case to exclude from the party check.
-- THROWs: 51363 booking not found, 51364 not a party to the booking,
-- 51365 this party cannot record this trigger.
CREATE OR ALTER PROCEDURE [Booking].[RecordNightStayBookingLocationEvent]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Trigger NVARCHAR(32),
    @CapturedByType NVARCHAR(16),
    @CapturedById UNIQUEIDENTIFIER,
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6),
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    SELECT @ProviderId = [ProviderId], @PetParentId = [PetParentId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51363, 'Night-stay booking was not found.', 1;
    END

    IF (@CapturedByType = N'Provider' AND @CapturedById <> @ProviderId)
       OR (@CapturedByType = N'Parent' AND @CapturedById <> @PetParentId)
       OR @CapturedByType NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51364, 'You are not a party to this booking.', 1;
    END

    IF @Trigger NOT IN (N'NoShowMarked', N'CashReceived', N'CashNotReceived', N'EvidenceCaptured')
    BEGIN
        THROW 51365, 'This trigger cannot be recorded on its own by this party.', 1;
    END

    DECLARE @Inserted TABLE ([NightStayBookingLocationEventId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingLocationEvents]
        ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
         [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
    OUTPUT inserted.[NightStayBookingLocationEventId] INTO @Inserted
    VALUES
        (@NightStayBookingId, @Trigger, @CapturedByType, @CapturedById,
         @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);

    SELECT [NightStayBookingLocationEventId], [NightStayBookingId], [Trigger], [CapturedByType],
           [CapturedById], [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc], [RecordedAtUtc]
    FROM [Booking].[NightStayBookingLocationEvents]
    WHERE [NightStayBookingLocationEventId] = (SELECT TOP (1) [NightStayBookingLocationEventId] FROM @Inserted);
END;

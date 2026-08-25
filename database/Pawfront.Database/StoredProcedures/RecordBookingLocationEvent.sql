-- Records one geolocation fix against a single-day booking, standalone — i.e. for
-- the moments that are NOT themselves a status transition, and therefore have no
-- transition sproc to ride along inside:
--   * CashNotReceived        the provider records that they were not paid. Log
--                            only: it changes no status and no payout field (see
--                            the endpoint notes) — the row IS the record.
--   * the PARENT's own fix   at a moment the PROVIDER drove (NoShowMarked,
--                            CashReceived, CashNotReceived, EvidenceCaptured).
--                            Each app reports its own position, so the parent's
--                            arrives on its own call rather than being copied off
--                            the provider's.
-- Every OTHER trigger is written inside the sproc that performs the transition
-- (StartBooking, VerifyBookingStartOtp, UpdateBookingStatus, MarkBookingPaid,
-- AddBookingEvidence, IssueBookingStartOtp), so a transition can never commit
-- while its evidence is lost to a separate failed call.
--
-- The acting party comes from the authenticated route, never the body, and is
-- checked against the booking here as well.
-- THROWs: 51360 booking not found, 51361 not a party to the booking,
-- 51362 this party cannot record this trigger.
CREATE OR ALTER PROCEDURE [Booking].[RecordBookingLocationEvent]
    @BookingId UNIQUEIDENTIFIER,
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
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51360, 'Booking was not found.', 1;
    END

    -- A Custom walk-in has no PetParentId, so the Parent branch can never match
    -- one — which is correct: there is no second party on a private job to have
    -- a location.
    IF (@CapturedByType = N'Provider' AND @CapturedById <> @ProviderId)
       OR (@CapturedByType = N'Parent' AND (@PetParentId IS NULL OR @CapturedById <> @PetParentId))
       OR @CapturedByType NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51361, 'You are not a party to this booking.', 1;
    END

    -- Defensive; the API validates first. Raised explicitly rather than left to
    -- CK_BookingLocationEvents_TriggerParty so a bad combination comes back as a
    -- typed error instead of a raw constraint violation.
    IF @Trigger NOT IN (N'NoShowMarked', N'CashReceived', N'CashNotReceived', N'EvidenceCaptured')
       OR (@Trigger = N'CashNotReceived' AND @CapturedByType NOT IN (N'Provider', N'Parent'))
    BEGIN
        THROW 51362, 'This trigger cannot be recorded on its own by this party.', 1;
    END

    DECLARE @Inserted TABLE ([BookingLocationEventId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingLocationEvents]
        ([BookingId], [Trigger], [CapturedByType], [CapturedById],
         [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
    OUTPUT inserted.[BookingLocationEventId] INTO @Inserted
    VALUES
        (@BookingId, @Trigger, @CapturedByType, @CapturedById,
         @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);

    SELECT [BookingLocationEventId], [BookingId], [Trigger], [CapturedByType], [CapturedById],
           [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc], [RecordedAtUtc]
    FROM [Booking].[BookingLocationEvents]
    WHERE [BookingLocationEventId] = (SELECT TOP (1) [BookingLocationEventId] FROM @Inserted);
END;

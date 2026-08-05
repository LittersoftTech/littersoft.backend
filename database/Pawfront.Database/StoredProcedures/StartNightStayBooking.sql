-- Provider taps "Start Job" on a multi-night booking: confirmed-equivalent →
-- START_JOB, issuing the parent-facing start-OTP atomically. Mirror of
-- [Booking].[StartBooking]: the stay's [CheckInDate] (the drop-off day — the
-- night-stay analogue of a single-day booking's service date) must be TODAY, and
-- the provider must be inside their own weekly working hours
-- ([Provider].[ProviderWeeklyAvailability]). The scheduled drop-off TIME is
-- deliberately not checked.
-- THROWs: 51251 not found, 51252 forbidden, 51253 not startable,
-- 51264 not the stay's check-in date, 51257 outside the provider's working hours.
CREATE OR ALTER PROCEDURE [Booking].[StartNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @CheckInDate = [CheckInDate]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51251, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51252, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51253, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The stay can only be started on its drop-off day. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error.
    IF @CheckInDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51264, 'The job can only be started on the day the booking is scheduled for.', 1;
    END

    -- The job can only be started while the provider is inside their own weekly
    -- working hours. 1970-01-04 was a Sunday, so the modulo gives 0 = Sunday
    -- (matching System.DayOfWeek / the [DayOfWeek] column) independently of DATEFIRST.
    DECLARE @DayOfWeek TINYINT = CAST(DATEDIFF(DAY, '19700104', CAST(@Now AS DATE)) % 7 AS TINYINT);
    DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
    DECLARE @IsOpen BIT, @OpensAt TIME(0), @ClosesAt TIME(0);

    SELECT @IsOpen = [IsOpen], @OpensAt = [StartTime], @ClosesAt = [EndTime]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId AND [DayOfWeek] = @DayOfWeek;

    -- A provider who has never saved their weekly hours is not gated.
    IF @IsOpen IS NOT NULL AND (@IsOpen = 0 OR @NowTime < @OpensAt OR @NowTime > @ClosesAt)
    BEGIN
        THROW 51257, 'The job can only be started during your working hours.', 1;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'START_JOB', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'START_JOB', N'Provider', @ProviderId, N'Job start requested; start code issued to parent');

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookingStartOtps]
        WHERE [NightStayBookingId] = @NightStayBookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] > @Now)
    BEGIN
        INSERT INTO [Booking].[NightStayBookingStartOtps]
            ([NightStayBookingId], [OtpCode], [ExpiresAtUtc])
        VALUES (@NightStayBookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
    END

    -- Mirror of Booking.StartBooking: the parent is told to open their code.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_START_OTP_ISSUED';

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;

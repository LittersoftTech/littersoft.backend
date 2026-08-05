-- Provider taps "Start Job" on a single-day booking: a transition from a
-- confirmed-equivalent (live) state to START_JOB that ALSO issues the parent-facing
-- start-OTP (@NewCode, @TtlMinutes) atomically. Two gates, both on "right now" (UTC):
-- the booking's own [BookingDate] must be TODAY, and the provider must be inside
-- their own weekly working hours ([Provider].[ProviderWeeklyAvailability]). The
-- time-of-day within the booked window is deliberately NOT checked — a provider
-- running early or late can still start the job, as long as it is the service day.
-- The parent reads the issued code to the provider, who enters it via
-- [Booking].[VerifyBookingStartOtp] to move the job to IN_PROGRESS.
-- THROWs: 51131 not found, 51132 forbidden, 51133 not in a startable state,
-- 51144 not the booking's service date, 51137 outside the provider's working hours.
CREATE OR ALTER PROCEDURE [Booking].[StartBooking]
    @BookingId UNIQUEIDENTIFIER,
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
    DECLARE @BookingDate DATE;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @BookingDate = [BookingDate]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51131, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51132, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The job can only be started on the day it is booked for. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error.
    IF @BookingDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51144, 'The job can only be started on the day the booking is scheduled for.', 1;
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
        THROW 51137, 'The job can only be started during your working hours.', 1;
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = N'START_JOB', [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'START_JOB', N'Provider', @ProviderId, N'Job start requested; start code issued to parent');

    -- Issue the start-OTP for the parent (reuse-while-valid).
    UPDATE [Booking].[BookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[BookingStartOtps]
        WHERE [BookingId] = @BookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] > @Now)
    BEGIN
        INSERT INTO [Booking].[BookingStartOtps]
            ([BookingId], [OtpCode], [ExpiresAtUtc])
        VALUES (@BookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
    END

    -- Tell the parent to open their start code. The provider tapped Start, so
    -- only the parent is notified. The code itself is deliberately NOT in the
    -- payload — a push is readable from a locked screen, and the whole point of
    -- the code is that the parent hands it over in person.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_START_OTP_ISSUED';

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;

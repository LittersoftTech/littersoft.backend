-- Verifies the provider-entered START-code and moves a single-day booking
-- START_JOB -> IN_PROGRESS. The start-OTP was issued to the parent when the
-- provider tapped "Start Job"; the parent reads it to the provider, who enters it
-- here. On success the OTP is consumed and the job is underway. Failed attempts
-- bump the OTP's FailedAttemptCount (committed even though the call then THROWs);
-- the 6th wrong attempt cancels the job — the booking is flipped to the terminal
-- OTP_MAX_ATTEMPTS_EXCEEDED status (freeing capacity, with a System audit row) and the
-- call THROWs 51136. THROWs: 51131 not found, 51132 forbidden, 51138 not START_JOB
-- (can't verify the start code), 51134 invalid/missing OTP, 51135 OTP expired,
-- 51136 too many wrong attempts (job cancelled).
--
-- This is the provider tapping "Proceed to start", so their geolocation is written
-- here — but ONLY on the success path, where the job actually starts. A wrong code
-- is not a job start, and the 6th failure ends the booking as
-- OTP_MAX_ATTEMPTS_EXCEEDED, which is not one of the moments being evidenced; the
-- fix supplied with those attempts is simply discarded. Recorded inside the same
-- transaction as the move to IN_PROGRESS, so the two cannot come apart.
CREATE OR ALTER PROCEDURE [Booking].[VerifyBookingStartOtp]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(6),
    -- The provider's position at the moment the job starts. See
    -- [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId]
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

    IF @CurrentStatus <> N'START_JOB'
    BEGIN
        THROW 51138, 'Booking is not awaiting a start code.', 1;
    END

    DECLARE @OtpId UNIQUEIDENTIFIER, @StoredCode NVARCHAR(6), @ExpiresAt DATETIME2(7), @FailedCount INT;
    SELECT TOP (1) @OtpId = [BookingStartOtpId], @StoredCode = [OtpCode], @ExpiresAt = [ExpiresAtUtc],
           @FailedCount = [FailedAttemptCount]
    FROM [Booking].[BookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
    ORDER BY [IssuedAtUtc] DESC;

    IF @OtpId IS NULL
    BEGIN
        THROW 51134, 'No active code. Ask the parent to open the booking to generate one.', 1;
    END

    IF @ExpiresAt <= @Now
    BEGIN
        UPDATE [Booking].[BookingStartOtps] SET [Status] = N'Expired' WHERE [BookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51135, 'The code has expired. Ask the parent to refresh the booking.', 1;
    END

    IF @StoredCode <> @OtpCode
    BEGIN
        DECLARE @NewFailedCount INT = @FailedCount + 1;

        UPDATE [Booking].[BookingStartOtps]
        SET [FailedAttemptCount] = @NewFailedCount,
            [Status] = CASE WHEN @NewFailedCount >= 6 THEN N'Expired' ELSE [Status] END
        WHERE [BookingStartOtpId] = @OtpId;

        IF @NewFailedCount >= 6
        BEGIN
            UPDATE [Booking].[Bookings]
            SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED', [UpdatedAtUtc] = @Now
            WHERE [BookingId] = @BookingId;

            INSERT INTO [Booking].[BookingStatusHistory]
                ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
            VALUES
                (@BookingId, @CurrentStatus, N'OTP_MAX_ATTEMPTS_EXCEEDED', N'System', NULL,
                 N'Job cancelled after 6 incorrect start-code attempts.');

            -- The provider entered the wrong codes, so only the parent is told
            -- their job is off. Enqueued before the COMMIT below so it lands in
            -- the same transaction as the cancellation — the THROW that follows
            -- is the API's 409, not a rollback.
            EXEC [Notification].[EnqueueBookingNotification]
                @BookingId = @BookingId,
                @IsNightStay = 0,
                @Audience = N'PetParent',
                @NotificationType = N'BOOKING_OTP_ATTEMPTS_EXCEEDED';

            COMMIT TRANSACTION;
            THROW 51136, 'Too many incorrect start-code attempts; the job has been cancelled.', 1;
        END

        COMMIT TRANSACTION;
        THROW 51134, 'The start code is incorrect.', 1;
    END

    -- Valid: consume the OTP and move the job to IN_PROGRESS.
    UPDATE [Booking].[BookingStartOtps]
    SET [Status] = N'Consumed', [ConsumedAtUtc] = @Now
    WHERE [BookingStartOtpId] = @OtpId;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'IN_PROGRESS', [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'IN_PROGRESS', N'Provider', @ProviderId, N'Job started with parent start-OTP');

    -- Where the provider was when the job actually started.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'JobStartProceeded', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- "Your job has started. Thank you for the OTP!" — the provider entered the
    -- code, so the parent is the one told.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_IN_PROGRESS';

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

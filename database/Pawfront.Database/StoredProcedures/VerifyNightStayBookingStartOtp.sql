-- Verifies the provider-entered START-code and moves a multi-night booking
-- START_JOB -> IN_PROGRESS. Mirror of [Booking].[VerifyBookingStartOtp]. The 6th
-- wrong attempt cancels the job (flips to OTP_MAX_ATTEMPTS_EXCEEDED, THROW 51256).
-- THROWs: 51251 not found, 51252 forbidden, 51258 not START_JOB, 51254
-- invalid/missing OTP, 51255 expired, 51256 too many wrong attempts.
-- Also records the provider's "Proceed to start" geolocation, on the SUCCESS path
-- only — see [Booking].[VerifyBookingStartOtp] for why a failed attempt is not one
-- of the moments being evidenced.
CREATE OR ALTER PROCEDURE [Booking].[VerifyNightStayBookingStartOtp]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(6),
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

    IF @CurrentStatus <> N'START_JOB'
    BEGIN
        THROW 51258, 'Booking is not awaiting a start code.', 1;
    END

    DECLARE @OtpId UNIQUEIDENTIFIER, @StoredCode NVARCHAR(6), @ExpiresAt DATETIME2(7), @FailedCount INT;
    SELECT TOP (1) @OtpId = [NightStayBookingStartOtpId], @StoredCode = [OtpCode], @ExpiresAt = [ExpiresAtUtc],
           @FailedCount = [FailedAttemptCount]
    FROM [Booking].[NightStayBookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
    ORDER BY [IssuedAtUtc] DESC;

    IF @OtpId IS NULL
    BEGIN
        THROW 51254, 'No active code. Ask the parent to open the booking to generate one.', 1;
    END

    IF @ExpiresAt <= @Now
    BEGIN
        UPDATE [Booking].[NightStayBookingStartOtps] SET [Status] = N'Expired' WHERE [NightStayBookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51255, 'The code has expired. Ask the parent to refresh the booking.', 1;
    END

    IF @StoredCode <> @OtpCode
    BEGIN
        DECLARE @NewFailedCount INT = @FailedCount + 1;

        UPDATE [Booking].[NightStayBookingStartOtps]
        SET [FailedAttemptCount] = @NewFailedCount,
            [Status] = CASE WHEN @NewFailedCount >= 6 THEN N'Expired' ELSE [Status] END
        WHERE [NightStayBookingStartOtpId] = @OtpId;

        IF @NewFailedCount >= 6
        BEGIN
            UPDATE [Booking].[NightStayBookings]
            SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED', [UpdatedAtUtc] = @Now
            WHERE [NightStayBookingId] = @NightStayBookingId;

            INSERT INTO [Booking].[NightStayBookingStatusHistory]
                ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
            VALUES
                (@NightStayBookingId, @CurrentStatus, N'OTP_MAX_ATTEMPTS_EXCEEDED', N'System', NULL,
                 N'Job cancelled after 6 incorrect start-code attempts.');

            -- Enqueued before the COMMIT so it shares the cancellation's
            -- transaction; the THROW below is the API's 409, not a rollback.
            EXEC [Notification].[EnqueueBookingNotification]
                @BookingId = @NightStayBookingId,
                @IsNightStay = 1,
                @Audience = N'PetParent',
                @NotificationType = N'BOOKING_OTP_ATTEMPTS_EXCEEDED';

            COMMIT TRANSACTION;
            THROW 51256, 'Too many incorrect start-code attempts; the job has been cancelled.', 1;
        END

        COMMIT TRANSACTION;
        THROW 51254, 'The start code is incorrect.', 1;
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Consumed', [ConsumedAtUtc] = @Now
    WHERE [NightStayBookingStartOtpId] = @OtpId;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'IN_PROGRESS', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'IN_PROGRESS', N'Provider', @ProviderId, N'Job started with parent start-OTP');

    -- Where the provider was when the stay actually started.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'JobStartProceeded', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_IN_PROGRESS';

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

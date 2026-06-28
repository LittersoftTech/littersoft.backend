-- Starts a single-day job after validating the provider-entered start-OTP.
-- The provider must be a party to the booking, the booking must be in a
-- confirmed-equivalent (live) state, and @OtpCode must match the active
-- (Pending, non-expired) OTP. On success the OTP is consumed and the booking
-- moves to JOB_STARTED with an audit row. Failed attempts bump the OTP's
-- FailedAttemptCount (committed even though the call then THROWs, so the
-- telemetry survives). THROWs: 51131 not found, 51132 forbidden, 51133 not in a
-- startable state, 51134 invalid/missing OTP, 51135 OTP expired.
CREATE OR ALTER PROCEDURE [Booking].[StartBookingWithOtp]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(6)
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

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be started from.', 1;
    END

    DECLARE @OtpId UNIQUEIDENTIFIER, @StoredCode NVARCHAR(6), @ExpiresAt DATETIME2(7);
    SELECT TOP (1) @OtpId = [BookingStartOtpId], @StoredCode = [OtpCode], @ExpiresAt = [ExpiresAtUtc]
    FROM [Booking].[BookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
    ORDER BY [IssuedAtUtc] DESC;

    IF @OtpId IS NULL
    BEGIN
        THROW 51134, 'No active start code. Ask the parent to open the booking to generate one.', 1;
    END

    IF @ExpiresAt <= @Now
    BEGIN
        UPDATE [Booking].[BookingStartOtps] SET [Status] = N'Expired' WHERE [BookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51135, 'The start code has expired. Ask the parent to refresh the booking.', 1;
    END

    IF @StoredCode <> @OtpCode
    BEGIN
        UPDATE [Booking].[BookingStartOtps]
        SET [FailedAttemptCount] = [FailedAttemptCount] + 1
        WHERE [BookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51134, 'The start code is incorrect.', 1;
    END

    -- Valid: consume the OTP and start the job.
    UPDATE [Booking].[BookingStartOtps]
    SET [Status] = N'Consumed', [ConsumedAtUtc] = @Now
    WHERE [BookingStartOtpId] = @OtpId;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'JOB_STARTED', [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'JOB_STARTED', N'Provider', @ProviderId, N'Job started with parent OTP');

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

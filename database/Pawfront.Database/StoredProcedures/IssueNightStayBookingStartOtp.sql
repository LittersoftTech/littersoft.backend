-- Issues (or reuses) the parent-facing start-OTP for a multi-night booking.
-- Mirror of [Booking].[IssueBookingStartOtp].
-- THROW 51250 booking not found.
-- Also records WHERE the parent was when the code was put on screen — see
-- [Booking].[IssueBookingStartOtp]. Night stays are App-only, so [PetParentId] is
-- always present.
CREATE OR ALTER PROCEDURE [Booking].[IssueNightStayBookingStartOtp]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @RowPetParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @RowPetParent = [PetParentId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @RowPetParent IS NULL
    BEGIN
        THROW 51250, 'Night stay booking was not found.', 1;
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    DECLARE @ActiveId UNIQUEIDENTIFIER;
    SELECT TOP (1) @ActiveId = [NightStayBookingStartOtpId]
    FROM [Booking].[NightStayBookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] > @Now
    ORDER BY [IssuedAtUtc] DESC;

    IF @ActiveId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([NightStayBookingStartOtpId] UNIQUEIDENTIFIER);
        INSERT INTO [Booking].[NightStayBookingStartOtps]
            ([NightStayBookingId], [OtpCode], [ExpiresAtUtc])
        OUTPUT inserted.[NightStayBookingStartOtpId] INTO @Inserted
        VALUES (@NightStayBookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));

        SELECT @ActiveId = [NightStayBookingStartOtpId] FROM @Inserted;
    END

    -- Mirror of Booking.IssueBookingStartOtp: record the parent's first sighting
    -- of the code, which is what separates the two nudge messages.
    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [SeenAtUtc] = COALESCE([SeenAtUtc], @Now)
    WHERE [NightStayBookingStartOtpId] = @ActiveId;

    -- Where the parent was when they showed the code. NOT collapsed to the first
    -- sighting the way [SeenAtUtc] is — each showing is a fresh position claim.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'StartOtpShown', N'Parent', @RowPetParent,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [NightStayBookingStartOtpId] AS [BookingStartOtpId], [NightStayBookingId] AS [BookingId],
           [OtpCode], [Status], [IssuedAtUtc], [ExpiresAtUtc]
    FROM [Booking].[NightStayBookingStartOtps]
    WHERE [NightStayBookingStartOtpId] = @ActiveId;

    COMMIT TRANSACTION;
END;

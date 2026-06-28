-- Issues (or reuses) the start-job OTP for a multi-night booking. Mirror of
-- [Booking].[IssueBookingStartOtp]. THROW 51250 booking not found.
CREATE OR ALTER PROCEDURE [Booking].[IssueNightStayBookingStartOtp]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [NightStayBookingId] = @NightStayBookingId)
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

    SELECT [NightStayBookingStartOtpId] AS [BookingStartOtpId], [NightStayBookingId] AS [BookingId],
           [OtpCode], [Status], [IssuedAtUtc], [ExpiresAtUtc]
    FROM [Booking].[NightStayBookingStartOtps]
    WHERE [NightStayBookingStartOtpId] = @ActiveId;

    COMMIT TRANSACTION;
END;

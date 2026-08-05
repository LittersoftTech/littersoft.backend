-- Completes a single-day job: IN_PROGRESS -> COMPLETED with an audit row. No OTP
-- is involved — the parent's verification code gates only the START_JOB ->
-- IN_PROGRESS transition ([Booking].[VerifyBookingStartOtp]); once the job is
-- underway the provider simply marks it done. ENDING (the retired "End Job"
-- intermediate state) is tolerated as a from-state so any legacy row parked
-- there can still be completed.
-- Completing the job is also what mints the booking's payout reference
-- ([PayoutId], 'PO-000123') and leaves [PayoutStatus] = 'Pending' — the provider
-- has earned the money but has not yet recorded receiving it. The subsequent
-- [Booking].[MarkBookingPaid] settles it to 'Paid'.
-- THROWs: 51131 not found, 51132 forbidden, 51133 not IN_PROGRESS (can't be
-- completed).
CREATE OR ALTER PROCEDURE [Booking].[CompleteBooking]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @PayoutId NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @Source = [Source], @PayoutId = [PayoutId]
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

    IF @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be completed from.', 1;
    END

    -- Mint the payout reference. Custom walk-ins are deliberately left unstamped:
    -- they are arranged off-platform, carry no Pawfront commission, and can never
    -- reach PAID (see 51163 in [Booking].[MarkBookingPaid]) — so a payout row for
    -- one would sit "awaiting payment" forever and skew the provider's earnings.
    -- COALESCE keeps this idempotent: a row that somehow already carries a
    -- reference keeps it rather than minting a second.
    IF @PayoutId IS NULL AND @Source = N'App'
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- [PayoutStatus] is not touched: it defaults to 'Pending' at insert and the
    -- IN_PROGRESS from-state guarantees nothing has moved it since.
    UPDATE [Booking].[Bookings]
    SET [Status] = N'COMPLETED',
        [UpdatedAtUtc] = @Now,
        [PayoutId] = COALESCE([PayoutId], @PayoutId)
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'COMPLETED', N'Provider', @ProviderId, N'Job completed by provider');

    -- The provider completed it, so only the parent is told — and the copy asks
    -- for the cash, since payment is always still pending at this point.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_COMPLETED';

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

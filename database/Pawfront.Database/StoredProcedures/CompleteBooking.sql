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
-- It is likewise where an EARLY finish releases the rest of the booked window
-- back to capacity, by stamping [ActualEndTime] (see the block below). The
-- booking's own [EndTime] — what was agreed and what is billed — is untouched.
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
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @EndTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @Source = [Source], @PayoutId = [PayoutId],
           @BookingDate = [BookingDate], @StartTime = [StartTime], @EndTime = [EndTime]
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

    -- Release the unused remainder of the booked window. A 6-hour day care that
    -- wrapped up after 3 hours kept the other 3 hours blocked, so nobody else
    -- could book them; stamping the real finish time frees them, because every
    -- capacity / slot / agenda query reads COALESCE([ActualEndTime], [EndTime]).
    --
    -- Left NULL — i.e. the booking keeps its whole window — unless the job
    -- genuinely ended early ON the service date:
    --   * completed at or after [EndTime]  -> nothing to give back;
    --   * completed on a LATER date (the job ran past midnight UTC, or the row
    --     was completed late) -> the window is long gone, and a bare TIME would
    --     compare as if it were on the booking date and wrongly free the slot.
    -- Clamped at [StartTime] for the case where the job ended before its booked
    -- start: starting a job is gated on the service DATE, not the time of day,
    -- so an early bird can finish before [StartTime]. That releases the slot
    -- entirely, which is the honest answer — the window is [StartTime, end).
    --
    -- NOTE: this does NOT re-price the booking. [EndTime] is what the parent
    -- agreed to and what [Booking].[BookingAmounts] and the detail read bill.
    DECLARE @ActualEndTime TIME(0) = NULL;

    IF CAST(@Now AS DATE) = @BookingDate
    BEGIN
        DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
        IF @NowTime < @EndTime
        BEGIN
            SET @ActualEndTime = CASE WHEN @NowTime < @StartTime THEN @StartTime ELSE @NowTime END;
        END
    END

    -- [PayoutStatus] is not touched: it defaults to 'Pending' at insert and the
    -- IN_PROGRESS from-state guarantees nothing has moved it since.
    UPDATE [Booking].[Bookings]
    SET [Status] = N'COMPLETED',
        [UpdatedAtUtc] = @Now,
        [ActualEndTime] = @ActualEndTime,
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

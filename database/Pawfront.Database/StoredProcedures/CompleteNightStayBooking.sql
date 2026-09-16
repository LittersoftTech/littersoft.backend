-- Completes a multi-night boarding job: IN_PROGRESS -> COMPLETED with an audit
-- row. Mirror of [Booking].[CompleteBooking] — no OTP is involved; the parent's
-- verification code gates only START_JOB -> IN_PROGRESS
-- ([Booking].[VerifyNightStayBookingStartOtp]). ENDING (the retired "End Job"
-- intermediate state) is tolerated as a from-state so any legacy row parked
-- there can still be completed.
-- Also mints the stay's payout reference ([PayoutId], 'PO-000123') and leaves
-- [PayoutStatus] = 'Pending', settled to 'Paid' by
-- [Booking].[MarkNightStayBookingPaid]. Night-stay bookings are always App
-- bookings, so — unlike the single-day mirror — there is no Custom exclusion.
-- It is likewise where an EARLY pickup releases the remaining nights back to
-- per-night capacity, by stamping [ActualCheckOutDate] (see the block below).
-- The stay's own [CheckOutDate] — what was agreed and billed — is untouched.
-- THROWs: 51251 not found, 51252 forbidden, 51253 not IN_PROGRESS (can't be
-- completed).
CREATE OR ALTER PROCEDURE [Booking].[CompleteNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @PayoutId NVARCHAR(64);
    DECLARE @CheckInDate DATE;
    DECLARE @CheckOutDate DATE;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @PayoutId = [PayoutId],
           @CheckInDate = [CheckInDate], @CheckOutDate = [CheckOutDate]
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

    IF @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51253, 'Booking is not in a state the job can be completed from.', 1;
    END

    -- Same payout namespace as single-day bookings (one shared SEQUENCE), so a
    -- 'PO-...' reference identifies a payout without needing the booking kind.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- Release the nights the pet did not end up staying. A 4-day / 3-night stay
    -- collected a day early kept that last night blocked against the provider's
    -- per-night capacity, so nobody else could board on it; recording the real
    -- pickup day frees it, because every per-night capacity / availability query
    -- reads COALESCE([ActualCheckOutDate], [CheckOutDate]).
    --
    -- The value carries the same [CheckInDate, X) meaning as [CheckOutDate]: it
    -- is the pickup DAY, not a stayed night. Completing on day D therefore means
    -- nights CheckInDate..D-1 were used, so D is the effective checkout.
    --
    -- Left NULL — the stay keeps every booked night — when completion lands on
    -- or after [CheckOutDate], since there is nothing to give back. Floored at
    -- one night: a stay wrapped up on the check-in day itself still consumed
    -- that night's place (the pet was there), and the CHECK constraint requires
    -- it. NOTE: this does NOT refund the stay — [CheckOutDate] is what was
    -- agreed and what [Booking].[BookingAmounts] bills nights against.
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ActualCheckOutDate DATE = NULL;

    IF @Today < @CheckOutDate
    BEGIN
        SET @ActualCheckOutDate =
            CASE WHEN @Today <= @CheckInDate THEN DATEADD(DAY, 1, @CheckInDate) ELSE @Today END;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'COMPLETED',
        [UpdatedAtUtc] = @Now,
        [ActualCheckOutDate] = @ActualCheckOutDate,
        [PayoutId] = COALESCE([PayoutId], @PayoutId)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'COMPLETED', N'Provider', @ProviderId, N'Job completed by provider');

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_COMPLETED';

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

-- Records that the parent has paid the provider for a single-day booking:
-- flips COMPLETED -> PAID, writes an audit row, and inserts the payment ledger
-- row in [Booking].[BookingPayments]. Provider-only, App bookings only (Custom
-- walk-ins are off-platform and never reach PAID), a booking is paid at most
-- once. @Amount / @PawfrontFee are computed by the app layer from the booking's
-- price-locked snapshot; @PaymentMethod is 'Cash' or 'Digital'.
-- THROWs: 51160 not found, 51161 not the provider, 51162 not COMPLETED,
-- 51163 Custom walk-in (App only), 51164 already paid.
--
-- This is the provider swiping "Cash Received", so their geolocation is written
-- here, in the same transaction as the ledger row — the two records of the same
-- moment must not be able to disagree about whether it happened. The PARENT's own
-- fix for this moment arrives separately, on their app's own call to
-- [Booking].[RecordBookingLocationEvent] with the same 'CashReceived' trigger.
CREATE OR ALTER PROCEDURE [Booking].[MarkBookingPaid]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2),
    @PaymentMethod NVARCHAR(16),
    -- The provider's position when the cash changed hands. See
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
    DECLARE @RowPetParent UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @PayoutId NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @RowPetParent = [PetParentId], @Source = [Source],
           @PayoutId = [PayoutId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51160, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51161, 'You are not the provider on this booking.', 1;
    END

    IF @Source <> N'App'
    BEGIN
        THROW 51163, 'Only app bookings can be marked paid.', 1;
    END

    IF @CurrentStatus = N'PAID'
    BEGIN
        THROW 51164, 'Booking is already marked paid.', 1;
    END

    IF @CurrentStatus <> N'COMPLETED'
    BEGIN
        THROW 51162, 'Booking must be completed before it can be marked paid.', 1;
    END

    -- The payout is normally minted at COMPLETED; stamp one here too so a booking
    -- completed before payout stamping shipped still ends up with a reference
    -- rather than a settled payout that has no id.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- Cash-only today, so recording the payment settles the payout in the same
    -- step: the parent handed the provider the money directly, there is no
    -- separate transfer leg to wait on.
    UPDATE [Booking].[Bookings]
    SET [Status] = N'PAID',
        [UpdatedAtUtc] = @Now,
        [PayoutId] = COALESCE([PayoutId], @PayoutId),
        [PayoutStatus] = N'Paid'
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'PAID', N'Provider', @ProviderId, N'Payment received from parent');

    INSERT INTO [Booking].[BookingPayments]
        ([BookingType], [BookingId], [ProviderId], [PetParentId], [Amount], [PawfrontFee], [PaymentMethod], [PaidAtUtc])
    VALUES
        (N'SingleDay', @BookingId, @ProviderId, @RowPetParent, @Amount, @PawfrontFee, @PaymentMethod, @Now);

    -- Where the provider was when they took the money.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'CashReceived', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- The provider recorded the cash, so the parent gets the receipt. The amount
    -- is the one just written to the ledger, not a re-derivation — the two must
    -- never disagree.
    DECLARE @AmountText NVARCHAR(64) =
        N'CHF ' + CONVERT(NVARCHAR(32), CAST(@Amount AS DECIMAL(12, 2)));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_PAID',
        @Amount = @AmountText;

    -- ...and the PROVIDER gets their invoice (V-S14). This is the moment it is
    -- settled: the job is COMPLETED, the cash is recorded, the payout above just
    -- flipped to 'Paid'. Both parties are notified because both did something —
    -- the relevance rule that suppresses a notification about your own action does
    -- not apply when the action closes out the other side's money too.
    --
    -- Same amount text as the receipt, from the ledger row rather than a second
    -- derivation: a provider's invoice and a parent's receipt for one payment
    -- disagreeing about the figure would be the worst possible bug here.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'Provider',
        @NotificationType = N'INVOICE_ISSUED',
        @Amount = @AmountText;

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

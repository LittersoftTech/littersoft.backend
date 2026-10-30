-- Records that the parent has paid the provider for a multi-night stay: flips
-- COMPLETED or PAYMENT_NOT_RECEIVED -> PAID, writes an audit row, and inserts the payment ledger row in
-- [Booking].[BookingPayments] (BookingType = 'NightStay'). Mirror of
-- [Booking].[MarkBookingPaid]. Night-stay bookings are always App bookings, so
-- there is no Custom check. Provider-only, paid at most once.
-- THROWs: 51280 not found, 51281 not the provider, 51282 not COMPLETED or PAYMENT_NOT_RECEIVED,
-- 51283 already paid.
-- Also records the provider's "Cash Received" geolocation — see
-- [Booking].[MarkBookingPaid]. The parent's own fix arrives separately, via
-- [Booking].[RecordNightStayBookingLocationEvent].
CREATE OR ALTER PROCEDURE [Booking].[MarkNightStayBookingPaid]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2),
    @PaymentMethod NVARCHAR(16),
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
    DECLARE @PayoutId NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @RowPetParent = [PetParentId], @PayoutId = [PayoutId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51280, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51281, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus = N'PAID'
    BEGIN
        THROW 51283, 'Booking is already marked paid.', 1;
    END

    IF @CurrentStatus NOT IN (N'COMPLETED', N'PAYMENT_NOT_RECEIVED')
    BEGIN
        THROW 51282, 'Booking must be completed before it can be marked paid.', 1;
    END

    -- Backstop for stays completed before payout stamping shipped — see the
    -- single-day mirror.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'PAID',
        [CashNotReceivedAtUtc] = NULL,
        [UpdatedAtUtc] = @Now,
        [PayoutId] = COALESCE([PayoutId], @PayoutId),
        [PayoutStatus] = N'Paid'
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'PAID', N'Provider', @ProviderId, N'Payment received from parent');

    INSERT INTO [Booking].[BookingPayments]
        ([BookingType], [BookingId], [ProviderId], [PetParentId], [Amount], [PawfrontFee], [PaymentMethod], [PaidAtUtc])
    VALUES
        (N'NightStay', @NightStayBookingId, @ProviderId, @RowPetParent, @Amount, @PawfrontFee, @PaymentMethod, @Now);

    -- Raise the two invoices this payment produces. See the twin in
    -- [Booking].[MarkBookingPaid] for why this sits inside the transaction.
    -- Returns no result set.
    EXEC [Billing].[RaiseBookingInvoices]
        @BookingType = N'NightStay',
        @BookingId = @NightStayBookingId,
        @ProviderId = @ProviderId,
        @PetParentId = @RowPetParent,
        @Amount = @Amount,
        @PawfrontFee = @PawfrontFee;

    -- Where the provider was when they took the money.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'CashReceived', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- The ledger's figure, not a re-derivation — the receipt must match the row.
    DECLARE @AmountText NVARCHAR(64) =
        N'CHF ' + CONVERT(NVARCHAR(32), CAST(@Amount AS DECIMAL(12, 2)));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_PAID',
        @Amount = @AmountText;

    -- V4 invoice copy promises a document. CompleteInvoiceGeneration sends
    -- V-S14 only after the provider's PDF is durable, with its 60-second delay.

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
           [PetId],
           [CashNotReceivedAtUtc], [CancellationReason],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Booking].[NightStayBookingStatusHistory] mh WHERE mh.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId] AND mh.[ToStatus] = N'PARENT_ACCEPTED_MODIFICATION') THEN 1 ELSE 0 END AS BIT) AS [ProviderModificationUsed],
           CASE WHEN [Booking].[NightStayBookings].[Status] = N'CREATED' THEN
               [Booking].[RequestExpiresAtUtc]([Booking].[NightStayBookings].[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate], COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)), NULL, NULL)
           WHEN [Booking].[NightStayBookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[RequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate], COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)), m.[ProposedCheckInDate], CASE WHEN m.[RequestedByActor]=N'Parent' AND m.[HasAcknowledgedTerms]=1 THEN COALESCE(m.[AcknowledgedDropOffTime],CAST('00:00' AS TIME)) ELSE COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)) END)
                FROM [Booking].[NightStayBookingModifications] m WHERE m.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'NightStay' AND pay.[BookingId]=[Booking].[NightStayBookings].[NightStayBookingId]) AS [PaidAtUtc]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;

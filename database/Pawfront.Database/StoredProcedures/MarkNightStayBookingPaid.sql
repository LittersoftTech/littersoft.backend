-- Records that the parent has paid the provider for a multi-night stay: flips
-- COMPLETED -> PAID, writes an audit row, and inserts the payment ledger row in
-- [Booking].[BookingPayments] (BookingType = 'NightStay'). Mirror of
-- [Booking].[MarkBookingPaid]. Night-stay bookings are always App bookings, so
-- there is no Custom check. Provider-only, paid at most once.
-- THROWs: 51280 not found, 51281 not the provider, 51282 not COMPLETED,
-- 51283 already paid.
CREATE OR ALTER PROCEDURE [Booking].[MarkNightStayBookingPaid]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2),
    @PaymentMethod NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @RowPetParent = [PetParentId]
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

    IF @CurrentStatus <> N'COMPLETED'
    BEGIN
        THROW 51282, 'Booking must be completed before it can be marked paid.', 1;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'PAID', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'PAID', N'Provider', @ProviderId, N'Payment received from parent');

    INSERT INTO [Booking].[BookingPayments]
        ([BookingType], [BookingId], [ProviderId], [PetParentId], [Amount], [PawfrontFee], [PaymentMethod], [PaidAtUtc])
    VALUES
        (N'NightStay', @NightStayBookingId, @ProviderId, @RowPetParent, @Amount, @PawfrontFee, @PaymentMethod, @Now);

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

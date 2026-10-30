-- External gateway callback. Flips PaymentStatus from N'Pending' to
-- N'Paid' (or N'Failed') and records the gateway reference.
--
-- Idempotency: if the booking is already in the target terminal state with
-- the same PaymentReference we return the row unchanged (no THROW). A second
-- callback with a DIFFERENT reference for an already-confirmed booking is a
-- programming error and throws 51093.
CREATE OR ALTER PROCEDURE [Event].[ConfirmEventBookingPayment]
    @BookingId UNIQUEIDENTIFIER,
    @PaymentStatus NVARCHAR(32),
    @PaymentReference NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @PaymentStatus NOT IN (N'Paid', N'Failed')
    BEGIN
        THROW 51094, 'PaymentStatus must be Paid or Failed.', 1;
    END

    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;


    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @CurrentReference NVARCHAR(200);
    SELECT @CurrentStatus = [PaymentStatus],
           @CurrentReference = [PaymentReference]
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51092, 'Event booking was not found.', 1;
    END

    -- Idempotent re-delivery: same terminal state + same reference is a no-op.
    IF @CurrentStatus = @PaymentStatus
       AND ISNULL(@CurrentReference, N'') = ISNULL(@PaymentReference, N'')
    BEGIN
        -- Fall through to the SELECT below.
        SET @PaymentStatus = @CurrentStatus;
    END
    ELSE IF @CurrentStatus IN (N'Paid', N'Failed')
    BEGIN
        THROW 51093, 'Event booking payment has already been confirmed with a different result.', 1;
    END
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE BookingId=@BookingId AND Status NOT IN (N'Confirmed',N'Attended'))
        THROW 51468, 'This booking cannot accept payment.', 1;

    UPDATE [Event].[EventBookings]
        SET [PaymentStatus]    = @PaymentStatus,
            [PaymentReference] = @PaymentReference,
            [UpdatedAtUtc]     = SYSUTCDATETIME()
        WHERE [BookingId] = @BookingId;
    END

    SELECT [BookingId],
           [EventId],
           [BookerName],
           [BookerEmail],
           [BookerMobile],
           [TicketCount],
           [PaymentMethod],
           [PaymentStatus],
           [PaymentReference],
           [TotalAmount],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;

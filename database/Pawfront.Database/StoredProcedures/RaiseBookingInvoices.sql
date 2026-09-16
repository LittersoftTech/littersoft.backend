-- Raises the TWO invoices a paid booking produces, at Status 'Pending'.
--
-- Called from INSIDE the transaction in [Booking].[MarkBookingPaid] and its
-- night-stay twin, immediately after the ledger row is written. That placement is
-- the whole design: the queue message that actually triggers rendering is sent
-- from C# once the transaction has committed, and can be lost to a crash in that
-- window — but these rows cannot be, so the sweep can always find work the queue
-- dropped. Same reasoning as [Notification].[EnqueueNotification] being called
-- from inside the transition sprocs rather than from an endpoint.
--
-- Returns NO RESULT SET, deliberately. A result set from a nested EXEC propagates
-- to the client, and both callers return the booking row as their own final
-- SELECT — an extra set here would break their C# readers. Same trap
-- [Notification].[EnqueueNotification] handles with @SuppressResultSet.
--
-- Idempotent: the caller already refuses a second payment (THROW 51164 / 51283),
-- but the UNIQUE (BookingType, BookingId, Recipient) key is what actually
-- guarantees one invoice per side, and the NOT EXISTS guards make a re-run a
-- no-op rather than a constraint violation.
--
-- @Amount / @PawfrontFee are the ledger's own figures, passed straight through
-- rather than re-derived. The invoice must show what was actually paid, so both
-- documents and the ledger row can never disagree — the same rule that makes the
-- caller reuse one amount for the receipt and the invoice notification.
CREATE OR ALTER PROCEDURE [Billing].[RaiseBookingInvoices]
    @BookingType NVARCHAR(16),
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    -- The issue YEAR is stamped into the reference; the counters themselves never
    -- reset, so this is presentation only and carries no rollover semantics.
    DECLARE @Year NVARCHAR(4) = CONVERT(NVARCHAR(4), DATEPART(YEAR, @Now));

    -- The pet parent's invoice: issued by the provider, for the service.
    IF NOT EXISTS (SELECT 1 FROM [Billing].[Invoices]
                   WHERE [BookingType] = @BookingType
                     AND [BookingId] = @BookingId
                     AND [Recipient] = N'PetParent')
    BEGIN
        DECLARE @ParentNumber BIGINT = NEXT VALUE FOR [Billing].[ParentInvoiceNumberSequence];

        INSERT INTO [Billing].[Invoices]
            ([InvoiceNumber], [BookingType], [BookingId], [Recipient],
             [ProviderId], [PetParentId], [Amount], [PawfrontFee],
             [Status], [IssuedAtUtc], [NextAttemptAtUtc], [CreatedAtUtc], [UpdatedAtUtc])
        VALUES
            (N'PF-INV-' + @Year + N'-' + FORMAT(@ParentNumber, N'D6'),
             @BookingType, @BookingId, N'PetParent',
             @ProviderId, @PetParentId, @Amount, @PawfrontFee,
             N'Pending', @Now, @Now, @Now, @Now);
    END

    -- The provider's invoice: issued by Littersoft GmbH, for the Pawfront fee on
    -- this one job. Raised even when the fee is zero — the document is the
    -- provider's record that the job was billed and that nothing was payable,
    -- which is exactly what the 100% launch discount is meant to show.
    IF NOT EXISTS (SELECT 1 FROM [Billing].[Invoices]
                   WHERE [BookingType] = @BookingType
                     AND [BookingId] = @BookingId
                     AND [Recipient] = N'Provider')
    BEGIN
        DECLARE @ProviderNumber BIGINT = NEXT VALUE FOR [Billing].[ProviderInvoiceNumberSequence];

        INSERT INTO [Billing].[Invoices]
            ([InvoiceNumber], [BookingType], [BookingId], [Recipient],
             [ProviderId], [PetParentId], [Amount], [PawfrontFee],
             [Status], [IssuedAtUtc], [NextAttemptAtUtc], [CreatedAtUtc], [UpdatedAtUtc])
        VALUES
            (N'LS-INV-' + @Year + N'-' + FORMAT(@ProviderNumber, N'D6'),
             @BookingType, @BookingId, N'Provider',
             @ProviderId, @PetParentId, @Amount, @PawfrontFee,
             N'Pending', @Now, @Now, @Now, @Now);
    END
END;

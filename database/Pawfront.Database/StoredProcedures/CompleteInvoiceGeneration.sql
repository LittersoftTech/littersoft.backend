-- Records the outcome of rendering ONE invoice.
--
-- Success stamps the blob URL and flips to 'Generated', which is what makes the
-- document downloadable — the URL is written only after the PDF is durable in
-- blob storage, so a row can never advertise a file that is not there.
--
-- Failure reschedules with exponential backoff (2/4/8/16/32 minutes, capped at
-- 60), mirroring [Notification].[CompleteNotificationDelivery], until
-- @MaxAttempts is reached — at which point the row settles as 'Failed' and stops
-- being swept. A 'Failed' invoice needs a human; it is deliberately not retried
-- forever, because the usual cause is missing reference data that no amount of
-- retrying will conjure.
--
-- Idempotent on success: re-completing an already-'Generated' invoice is a no-op
-- rather than an error, so a redelivered queue message cannot overwrite a good
-- URL or reset the clock.
--
-- IT IS ALSO WHERE THE PARENT'S 'INVOICE_ISSUED' PUSH IS ENQUEUED, and that is a
-- deliberate departure from every other booking notification, which fires from the
-- transition that caused it. This one cannot: at [Booking].[MarkBookingPaid] the
-- invoice row exists but its PDF does not — rendering is asynchronous — so a push
-- sent there would say "your invoice is ready, tap to view" about a document that
-- answers 409 InvoiceNotReady. Sending it here makes the notification true the
-- moment it is sent, and atomic with the URL that makes it true.
--
-- The PROVIDER's INVOICE_ISSUED is unaffected and still fires from mark-paid: its
-- copy routes to the booking summary, not to a document, so it has no such
-- dependency.
CREATE OR ALTER PROCEDURE [Billing].[CompleteInvoiceGeneration]
    @InvoiceId UNIQUEIDENTIFIER,
    @Succeeded BIT,
    @InvoiceUrl NVARCHAR(1000) = NULL,
    @Error NVARCHAR(2000) = NULL,
    @MaxAttempts INT = 5,
    -- The provider's BUSINESS name, resolved by the renderer from their Cosmos
    -- offering document (unreachable from SQL) and passed back in so the push
    -- names the same issuer the PDF prints. NULL falls back to "your provider".
    @IssuedBy NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    IF @Succeeded = 1
    BEGIN
        IF @InvoiceUrl IS NULL OR LTRIM(RTRIM(@InvoiceUrl)) = N''
        BEGIN
            THROW 51400, 'A generated invoice must carry a URL.', 1;
        END

        DECLARE @Notify TABLE
        (
            [BookingType] NVARCHAR(16),
            [BookingId] UNIQUEIDENTIFIER,
            [Recipient] NVARCHAR(16),
            [InvoiceNumber] NVARCHAR(64),
            [Amount] DECIMAL(10, 2)
        );

        -- OUTPUT rather than a separate read, so only a row this call actually
        -- transitioned is notified. A redelivered message finds the invoice already
        -- 'Generated', updates nothing, captures nothing, and sends no second push
        -- — the idempotency that matters, since the outbox dedupe key would catch a
        -- duplicate but the inbox would still have shown one.
        UPDATE [Billing].[Invoices]
        SET [Status] = N'Generated',
            [InvoiceUrl] = @InvoiceUrl,
            [GeneratedAtUtc] = @Now,
            [LastError] = NULL,
            [UpdatedAtUtc] = @Now
        OUTPUT [inserted].[BookingType], [inserted].[BookingId], [inserted].[Recipient],
               [inserted].[InvoiceNumber], [inserted].[Amount]
        INTO @Notify ([BookingType], [BookingId], [Recipient], [InvoiceNumber], [Amount])
        WHERE [InvoiceId] = @InvoiceId
          AND [Status] <> N'Generated';

        -- Only the PARENT's document is announced. The provider was already told at
        -- mark-paid, and telling them twice about one job would be noise.
        DECLARE @BookingType NVARCHAR(16);
        DECLARE @BookingId UNIQUEIDENTIFIER;
        DECLARE @InvoiceNumber NVARCHAR(64);
        DECLARE @Amount DECIMAL(10, 2);

        SELECT @BookingType = [BookingType],
               @BookingId = [BookingId],
               @InvoiceNumber = [InvoiceNumber],
               @Amount = [Amount]
        FROM @Notify
        WHERE [Recipient] = N'PetParent';

        IF @BookingId IS NOT NULL
        BEGIN
            -- The invoice's OWN frozen amount, not a re-derivation: the push and
            -- the document it points at must quote the same figure.
            DECLARE @AmountText NVARCHAR(64) =
                N'CHF ' + CONVERT(NVARCHAR(32), CAST(@Amount AS DECIMAL(12, 2)));

            -- Hoisted into a variable: T-SQL allows only a constant or a variable
            -- as an EXEC argument, never an expression.
            DECLARE @IsNightStay BIT =
                CASE WHEN @BookingType = N'NightStay' THEN 1 ELSE 0 END;

            EXEC [Notification].[EnqueueBookingNotification]
                @BookingId = @BookingId,
                @IsNightStay = @IsNightStay,
                @Audience = N'PetParent',
                @NotificationType = N'INVOICE_ISSUED',
                @Amount = @AmountText,
                @InvoiceId = @InvoiceNumber,
                @IssuedBy = @IssuedBy;
        END

        RETURN;
    END

    -- Backoff doubles per attempt from 2 minutes and is capped, so a persistent
    -- failure stops hammering the renderer without ever going silent.
    UPDATE [Billing].[Invoices]
    SET [Status] = CASE WHEN [AttemptCount] >= @MaxAttempts THEN N'Failed' ELSE N'Pending' END,
        [NextAttemptAtUtc] = CASE
            WHEN [AttemptCount] >= @MaxAttempts THEN [NextAttemptAtUtc]
            ELSE DATEADD(MINUTE,
                         CASE WHEN POWER(2, [AttemptCount]) > 60 THEN 60
                              ELSE POWER(2, [AttemptCount]) END,
                         @Now)
        END,
        [LastError] = @Error,
        [UpdatedAtUtc] = @Now
    WHERE [InvoiceId] = @InvoiceId
      AND [Status] <> N'Generated';
END;

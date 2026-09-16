-- One row per INVOICE — and a paid booking raises TWO of them, because two
-- different parties bill two different things for the same job:
--
--   Recipient = 'PetParent' -> issued BY the provider TO the pet parent for the
--               service itself. Pawfront only delivers it on the provider's
--               behalf; the document is not Pawfront's.
--   Recipient = 'Provider'  -> issued BY Littersoft GmbH TO the provider for the
--               Pawfront service fee earned on that one job. It exists because
--               cash jobs hand the provider the whole amount, fee included, so
--               the fee has to be billed back. Once digital payouts land the same
--               fee is netted off the payout instead and this half goes away.
--
-- [BookingType] discriminates which booking table [BookingId] points at, exactly
-- as it does on [Booking].[BookingPayments], and for the same reason there is
-- deliberately NO FK: one column cannot reference two tables. The pairing with
-- the ledger is intentional — an invoice is the document for a payment, so the
-- two are keyed the same way and can always be joined.
--
-- The row is INSERTED by [Booking].[MarkBookingPaid] (and its night-stay twin)
-- INSIDE the transaction that flips the booking to PAID, at Status 'Pending'.
-- That is what makes the PDF durable: the queue message that triggers rendering
-- is sent from C# AFTER that transaction commits and could be lost to a crash,
-- but the Pending row cannot be — a sweep re-enqueues anything left behind. Same
-- outbox reasoning as [Notification].[NotificationOutbox], and the same posture:
-- the row IS the job, not a copy of it.
--
-- [InvoiceNumber] is minted from a SEQUENCE at INSERT rather than at render time,
-- so it is race-free and a re-render can never change the number on a document
-- somebody has already been sent. A burned number on an invoice that never
-- renders is normal and harmless.
CREATE TABLE [Billing].[Invoices]
(
    [InvoiceId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Invoices_Id] DEFAULT NEWSEQUENTIALID(),

    -- 'PF-INV-2026-004812' (parent) / 'LS-INV-2026-001947' (provider). The year
    -- is the issue year stamped in; the counter itself never resets, so a
    -- reference is unique for the life of the system.
    [InvoiceNumber] NVARCHAR(64) NOT NULL,

    [BookingType] NVARCHAR(16) NOT NULL,
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [Recipient] NVARCHAR(16) NOT NULL,

    -- Both parties are recorded on both invoices: each document names an issuer
    -- and a recipient, and the download endpoints scope by whichever id belongs
    -- to the calling host.
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,

    -- Frozen at PAID from the ledger row, NOT re-derived at render time. A
    -- re-rendered invoice must show the figure the parent actually paid, even if
    -- the fee percentage or the provider's offering has changed since — the same
    -- price-lock reasoning that governs the booking itself.
    [Amount] DECIMAL(10, 2) NOT NULL,
    [PawfrontFee] DECIMAL(10, 2) NOT NULL
        CONSTRAINT [DF_Invoices_PawfrontFee] DEFAULT 0,

    -- 'Pending'   - raised, not yet rendered (the sweep's territory)
    -- 'Generating'- claimed by a renderer, lease held via [NextAttemptAtUtc]
    -- 'Generated' - [InvoiceUrl] is populated and downloadable
    -- 'Failed'    - gave up after [MaxAttempts]; needs a human
    [Status] NVARCHAR(16) NOT NULL
        CONSTRAINT [DF_Invoices_Status] DEFAULT N'Pending',

    -- Blob URL under the [invoices] container, folder = BookingId. NULL until
    -- rendered.
    [InvoiceUrl] NVARCHAR(1000) NULL,

    [IssuedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Invoices_IssuedAtUtc] DEFAULT SYSUTCDATETIME(),
    [GeneratedAtUtc] DATETIME2(7) NULL,

    -- Retry bookkeeping, mirroring the notification outbox. [NextAttemptAtUtc]
    -- doubles as the claim lease: a renderer that dies mid-job releases its row
    -- automatically when the lease lapses, rather than stranding it.
    [AttemptCount] INT NOT NULL
        CONSTRAINT [DF_Invoices_AttemptCount] DEFAULT 0,
    [NextAttemptAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Invoices_NextAttemptAtUtc] DEFAULT SYSUTCDATETIME(),
    [LastError] NVARCHAR(2000) NULL,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Invoices_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Invoices_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Invoices] PRIMARY KEY CLUSTERED ([InvoiceId] ASC),

    -- One invoice per side per booking. This is what makes the INSERT in
    -- [Booking].[MarkBookingPaid] safe to re-run and what stops a duplicate
    -- queue message raising a second document for the same job.
    CONSTRAINT [UQ_Invoices_BookingRecipient]
        UNIQUE ([BookingType], [BookingId], [Recipient]),
    CONSTRAINT [UQ_Invoices_InvoiceNumber] UNIQUE ([InvoiceNumber]),

    CONSTRAINT [CK_Invoices_BookingType]
        CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_Invoices_Recipient]
        CHECK ([Recipient] IN (N'PetParent', N'Provider')),
    CONSTRAINT [CK_Invoices_Status]
        CHECK ([Status] IN (N'Pending', N'Generating', N'Generated', N'Failed')),
    CONSTRAINT [CK_Invoices_Amount] CHECK ([Amount] >= 0),
    CONSTRAINT [CK_Invoices_PawfrontFee] CHECK ([PawfrontFee] >= 0),
    -- A 'Generated' invoice without a URL would be downloadable in name only.
    CONSTRAINT [CK_Invoices_GeneratedHasUrl]
        CHECK ([Status] <> N'Generated' OR [InvoiceUrl] IS NOT NULL)
);

GO

-- Drives the sweep's "what did the queue miss?" scan. Filtered to the two
-- unfinished states so it stays tiny however many invoices accumulate — the
-- whole point is that the steady state of this index is empty.
CREATE INDEX [IX_Invoices_Unfinished]
    ON [Billing].[Invoices] ([NextAttemptAtUtc])
    INCLUDE ([BookingType], [BookingId], [Recipient], [AttemptCount])
    WHERE [Status] IN (N'Pending', N'Generating');

GO

-- Backs the two "my invoices" reads. Each host only ever asks about its own
-- side, so the recipient is part of the key rather than a filter applied after.
CREATE INDEX [IX_Invoices_Provider]
    ON [Billing].[Invoices] ([ProviderId], [Recipient])
    INCLUDE ([BookingType], [BookingId], [Status], [InvoiceUrl], [IssuedAtUtc]);

GO

CREATE INDEX [IX_Invoices_PetParent]
    ON [Billing].[Invoices] ([PetParentId], [Recipient])
    INCLUDE ([BookingType], [BookingId], [Status], [InvoiceUrl], [IssuedAtUtc]);

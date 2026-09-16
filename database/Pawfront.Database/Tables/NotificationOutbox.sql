-- Transactional outbox for push notifications, and the read-model behind the
-- in-app notification inbox — one row is BOTH the delivery job and the inbox
-- entry, so a notification is stored exactly once.
--
-- Rows are written in the SAME transaction as the domain change that caused them
-- (a booking status flip, a ticket purchase, a sweep settling a job), which is the
-- point of the design: the three processes that change a booking's status
-- (provider API, parent API, and the Pawfront.Functions sweep) all enqueue the
-- same way, and none of them has to make an outbound HTTP call on the request
-- path. NotificationDispatchFunction claims batches from here and sends them to
-- FCM.
--
-- [Title] / [Body] / [Route] are deliberately NULL at enqueue time. The dispatcher
-- renders them from the C# NotificationTemplateCatalog using [NotificationType] +
-- [DataJson] and writes them back on completion, so all user-facing copy lives in
-- ONE place rather than being duplicated across C# call sites and T-SQL sweeps.
-- The inbox therefore only lists rows that have been rendered ([Title] IS NOT NULL).
--
-- NO FK to [Provider].[Providers] / [Parent].[PetParents] — same posture as the
-- [Booking].[BookingPayments] ledger. An anonymised ("deleted") account must not
-- cascade-delete its notification history, and [RecipientId] is polymorphic
-- (discriminated by [Audience]) so it could not carry one anyway.
CREATE TABLE [Notification].[NotificationOutbox]
(
    [NotificationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NotificationOutbox_Id] DEFAULT NEWSEQUENTIALID(),

    -- Which app (and therefore which Firebase project) this is destined for.
    -- 'Provider' -> [Provider].[ProviderDeviceTokens] keyed by ProviderId;
    -- 'PetParent' -> [Parent].[ParentDeviceTokens] keyed by PetParentId.
    [Audience] NVARCHAR(16) NOT NULL,
    [RecipientId] UNIQUEIDENTIFIER NOT NULL,

    -- Stable machine key (e.g. N'BOOKING_ACCEPTED'). The mobile app branches on
    -- this; the dispatcher renders copy from it. Never localise or reword it.
    [NotificationType] NVARCHAR(64) NOT NULL,

    -- Rendered by the dispatcher (see the note above) — NULL until then.
    [Title] NVARCHAR(200) NULL,
    [Body] NVARCHAR(1000) NULL,
    [Route] NVARCHAR(200) NULL,

    -- What the notification is about, for the app's deep link.
    [EntityType] NVARCHAR(32) NULL,
    [EntityId] UNIQUEIDENTIFIER NULL,

    -- Flat string -> string JSON object. Feeds BOTH the template renderer and the
    -- FCM `data` payload. Flat and string-valued because FCM rejects non-string
    -- data values, so nested objects would have to be re-stringified anyway.
    [DataJson] NVARCHAR(MAX) NULL,

    -- Optional rich image (Android bigPicture / iOS attachment). Must be publicly
    -- fetchable — the OS downloads it anonymously, so a bare private-container
    -- blob URL will NOT work.
    [ImageUrl] NVARCHAR(1000) NULL,

    -- Pending -> Sending (claimed, lease held) -> Sent | NoDevice | Failed.
    -- 'NoDevice' is a success: the notification is real and belongs in the inbox,
    -- the recipient just has no active device token to push it to.
    [Status] NVARCHAR(16) NOT NULL
        CONSTRAINT [DF_NotificationOutbox_Status] DEFAULT N'Pending',
    [AttemptCount] INT NOT NULL
        CONSTRAINT [DF_NotificationOutbox_AttemptCount] DEFAULT 0,
    -- Doubles as the retry-backoff time AND the claim lease expiry: claiming
    -- pushes it forward, so a dispatcher that crashes mid-flight releases the row
    -- automatically once the lease lapses.
    [NextAttemptAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NotificationOutbox_NextAttemptAtUtc] DEFAULT SYSUTCDATETIME(),
    [LastError] NVARCHAR(2000) NULL,
    -- How many device tokens the last successful send reached (0 for 'NoDevice').
    [DeliveredCount] INT NOT NULL
        CONSTRAINT [DF_NotificationOutbox_DeliveredCount] DEFAULT 0,

    -- Idempotency key, e.g. N'BOOKING_ACCEPTED:<bookingId>'. The sweeps run every
    -- 5 minutes and the inbox makes duplicates user-visible, so a re-enqueue must
    -- collapse onto the existing row rather than creating a second one.
    [DedupeKey] NVARCHAR(200) NULL,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NotificationOutbox_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NotificationOutbox_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [SentAtUtc] DATETIME2(7) NULL,
    [ReadAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_NotificationOutbox] PRIMARY KEY CLUSTERED ([NotificationId] ASC),
    CONSTRAINT [CK_NotificationOutbox_Audience]
        CHECK ([Audience] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_NotificationOutbox_Status]
        CHECK ([Status] IN (N'Pending', N'Sending', N'Sent', N'NoDevice', N'Failed'))
);

GO

-- The dispatcher's claim predicate.
CREATE INDEX [IX_NotificationOutbox_Dispatch]
    ON [Notification].[NotificationOutbox] ([Status], [NextAttemptAtUtc])
    INCLUDE ([Audience], [RecipientId], [AttemptCount]);

GO

-- The inbox list: newest-first for one recipient.
CREATE INDEX [IX_NotificationOutbox_Inbox]
    ON [Notification].[NotificationOutbox] ([Audience], [RecipientId], [CreatedAtUtc] DESC)
    INCLUDE ([ReadAtUtc], [Title]);

GO

-- Idempotency backstop behind the explicit pre-check in Notification.EnqueueNotification.
CREATE UNIQUE INDEX [UX_NotificationOutbox_DedupeKey]
    ON [Notification].[NotificationOutbox] ([DedupeKey])
    WHERE [DedupeKey] IS NOT NULL;

-- One chat thread between a provider and a pet parent.
--
-- The UNIQUE on ([ProviderId], [PetParentId]) is the load-bearing constraint: it
-- makes "one thread per pair" a database fact rather than an application
-- convention, which is what lets [Chat].[GetOrCreateConversation] be race-safe
-- with a lock over that one key instead of a broader guard.
--
-- NO FK to [Provider].[Providers] / [Parent].[PetParents] — the same posture as
-- [Booking].[BookingPayments] and [Notification].[NotificationOutbox]. An
-- anonymised ("deleted") account keeps its conversations: the counterparty was
-- part of those exchanges too, and cascading them away would destroy their
-- record, not just the leaver's. The name shown against a deleted account comes
-- from a LIVE join in [Chat].[ListConversations], so it correctly reads
-- "Deleted Provider" / "Deleted User" rather than a frozen real name.
--
-- The last-message columns are a denormalised cache of the newest message, kept
-- so the inbox list is one indexed read per participant instead of a fan-out of
-- Cosmos queries. They are written by [Chat].[CommitMessageAppend] in the same
-- transaction that advances [LastSequence].
CREATE TABLE [Chat].[Conversations]
(
    [ConversationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Conversations_ConversationId] DEFAULT NEWSEQUENTIALID(),

    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,

    -- Monotonic per conversation, assigned by [Chat].[ReserveMessageSequence]. It orders
    -- the thread and is the cursor the history read pages back through
    -- (?beforeSequence=), which is why ordering is by sequence and not by
    -- timestamp: two messages can share a millisecond, but never a sequence.
    --
    -- A HIGH-WATER MARK, not a count. The message body is written to Cosmos AFTER
    -- this is advanced, so a crash in between burns a number and leaves a gap.
    -- Gaps are harmless here, which is precisely why there is no [MessageCount]
    -- column: it would be a value that could silently become wrong.
    [LastSequence] BIGINT NOT NULL
        CONSTRAINT [DF_Conversations_LastSequence] DEFAULT 0,

    -- Newest-message cache for the inbox card. NULL until the first message: a
    -- conversation exists from the moment somebody opens it, which can be before
    -- anything is said.
    [LastMessageAtUtc] DATETIME2(7) NULL,
    [LastMessagePreview] NVARCHAR(200) NULL,
    [LastMessageSenderType] NVARCHAR(16) NULL,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Conversations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Conversations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Conversations] PRIMARY KEY CLUSTERED ([ConversationId] ASC),
    CONSTRAINT [UQ_Conversations_Pair] UNIQUE ([ProviderId], [PetParentId]),
    CONSTRAINT [CK_Conversations_LastMessageSenderType]
        CHECK ([LastMessageSenderType] IS NULL
               OR [LastMessageSenderType] IN (N'Provider', N'PetParent'))
);

GO

-- The two inbox reads: "my conversations, most recently active first". One index
-- per side because the two participants live in different columns.
CREATE INDEX [IX_Conversations_Provider_LastMessage]
    ON [Chat].[Conversations] ([ProviderId], [LastMessageAtUtc] DESC)
    INCLUDE ([PetParentId], [LastMessagePreview], [LastMessageSenderType], [LastSequence]);

GO

CREATE INDEX [IX_Conversations_PetParent_LastMessage]
    ON [Chat].[Conversations] ([PetParentId], [LastMessageAtUtc] DESC)
    INCLUDE ([ProviderId], [LastMessagePreview], [LastMessageSenderType], [LastSequence]);

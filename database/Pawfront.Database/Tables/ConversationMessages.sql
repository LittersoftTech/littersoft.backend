-- One row per message, holding everything about it that SQL has to be the
-- authority on: its sequence, and whether its body was ever durably written.
--
-- WHY THIS TABLE EXISTS. It is NOT a copy of the message — the body still lives
-- in Cosmos, partitioned by conversation, which is where the volume belongs.
-- This is the ledger that makes sending a message atomic and exactly-once, and it
-- pays for itself twice over:
--
--   1. IDEMPOTENCY. [PK_ConversationMessages] on ([ConversationId], [MessageId])
--      makes "one message per client id per thread" a database fact rather than
--      an application convention. The client mints [MessageId] before sending, so
--      a retry — over the hub, over REST, or one of each — collides on the
--      primary key inside [Chat].[ReserveMessageSequence] and is handed the
--      ORIGINAL sequence instead of taking a new one. Previously this was left
--      entirely to the Cosmos document id, which cannot help when the Cosmos
--      write is the very thing that failed.
--
--   2. ATOMICITY. A send is two SQL calls around the Cosmos write —
--      [Chat].[ReserveMessageSequence], then the body, then
--      [Chat].[CommitMessageAppend]. Reserve assigns the sequence and nothing
--      else; every observable effect (the inbox preview, the recipient's unread
--      count, their push) belongs to commit. So a body that never lands leaves
--      NOTHING a client can see, and [CommittedAtUtc] is how the second call
--      knows whether the first already ran.
--
-- The one residue of a failed send is a spent sequence number. That is a gap, and
-- gaps have always been harmless here — see the note on
-- [Chat].[Conversations].[LastSequence] for why nothing counts them.
--
-- ON DELETE CASCADE from [Chat].[Conversations], matching
-- [Chat].[ConversationParticipants]: these rows describe a thread and have no
-- meaning without it. Note that is a different judgement from the one the
-- conversation itself makes about a deleted ACCOUNT, which it deliberately
-- survives.
CREATE TABLE [Chat].[ConversationMessages]
(
    [ConversationId] UNIQUEIDENTIFIER NOT NULL,

    -- Client-generated, and also the Cosmos document id. See the class remarks on
    -- ChatMessageDocument for why those are the same value.
    [MessageId] UNIQUEIDENTIFIER NOT NULL,

    [Sequence] BIGINT NOT NULL,

    -- Copied from the reservation so [Chat].[CommitMessageAppend] can work out who
    -- to notify without being told again by the caller. Trusting a caller-supplied
    -- sender on the second call would let the two phases disagree about who sent
    -- the message.
    [SenderType] NVARCHAR(16) NOT NULL,
    [SenderId] UNIQUEIDENTIFIER NOT NULL,

    -- When the send was accepted. This is the message's [CreatedAtUtc] — the
    -- Cosmos document is written between the two phases, so the timestamp has to
    -- come from the first one.
    [ReservedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ConversationMessages_ReservedAtUtc] DEFAULT SYSUTCDATETIME(),

    -- NULL until the body is durable. A row that stays NULL is a send that failed
    -- between the two phases; nothing reads it except a retry of the same
    -- [MessageId], which reuses its sequence and completes it.
    [CommittedAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_ConversationMessages]
        PRIMARY KEY CLUSTERED ([ConversationId] ASC, [MessageId] ASC),

    -- Two messages in a thread can never share a sequence. Belt and braces behind
    -- the UPDLOCK in [Chat].[ReserveMessageSequence], and the index the
    -- "is this still the newest committed message?" check seeks on.
    CONSTRAINT [UQ_ConversationMessages_Sequence]
        UNIQUE ([ConversationId], [Sequence]),

    CONSTRAINT [FK_ConversationMessages_Conversations_ConversationId]
        FOREIGN KEY ([ConversationId]) REFERENCES [Chat].[Conversations] ([ConversationId])
        ON DELETE CASCADE,

    CONSTRAINT [CK_ConversationMessages_SenderType]
        CHECK ([SenderType] IN (N'Provider', N'PetParent'))
);

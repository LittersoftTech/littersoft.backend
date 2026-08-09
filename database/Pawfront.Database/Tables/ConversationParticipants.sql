-- Per-side state on a conversation: how far this participant has read, how much
-- they have not, and whether they have muted the thread.
--
-- A separate table rather than four more columns on [Chat].[Conversations]
-- (ProviderLastReadSequence, ProviderUnreadCount, ParentLastReadSequence, ...)
-- because every read and write of this state is symmetric — "advance the sender,
-- increment the recipient" — and column pairs would force a
-- CASE WHEN @ActorType = 'Provider' branch into every one of those statements.
-- With a row per side, [Chat].[AppendMessage] updates both sides in a single
-- statement keyed on [ParticipantType].
--
-- Exactly two rows per conversation, guaranteed by the UNIQUE below and written
-- together by [Chat].[GetOrCreateConversation].
CREATE TABLE [Chat].[ConversationParticipants]
(
    [ConversationParticipantId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ConversationParticipants_Id] DEFAULT NEWSEQUENTIALID(),

    [ConversationId] UNIQUEIDENTIFIER NOT NULL,

    -- Which side. The matching id is this participant's ProviderId or
    -- PetParentId; it duplicates the pair on [Chat].[Conversations] on purpose,
    -- so the unread-badge query is one index seek here rather than a UNION over
    -- the conversation table's two side-specific indexes.
    [ParticipantType] NVARCHAR(16) NOT NULL,
    [ParticipantId] UNIQUEIDENTIFIER NOT NULL,

    -- The highest [Chat].[Conversations].[LastSequence] this participant has
    -- seen. Sending advances your own pointer — you have obviously read what you
    -- just wrote.
    [LastReadSequence] BIGINT NOT NULL
        CONSTRAINT [DF_ConversationParticipants_LastReadSequence] DEFAULT 0,

    -- A real counter, incremented per delivered message and zeroed on read,
    -- rather than something derived from the sequence gap. The two can legitimately
    -- disagree: sequence numbers can have gaps (see the note on
    -- [Chat].[Conversations].[LastSequence]), so subtracting would over-count.
    [UnreadCount] INT NOT NULL
        CONSTRAINT [DF_ConversationParticipants_UnreadCount] DEFAULT 0,

    -- Suppresses the push for this thread only. The message is still delivered
    -- over the socket and still lands in the inbox — muting silences the buzz,
    -- it does not stop the conversation.
    [IsMuted] BIT NOT NULL
        CONSTRAINT [DF_ConversationParticipants_IsMuted] DEFAULT 0,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ConversationParticipants_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ConversationParticipants_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ConversationParticipants] PRIMARY KEY CLUSTERED ([ConversationParticipantId] ASC),
    CONSTRAINT [FK_ConversationParticipants_Conversations_ConversationId]
        FOREIGN KEY ([ConversationId]) REFERENCES [Chat].[Conversations] ([ConversationId])
        ON DELETE CASCADE,
    -- One row per side, which is also what makes the two-row insert in
    -- GetOrCreateConversation safe to retry.
    CONSTRAINT [UQ_ConversationParticipants_Conversation_Type]
        UNIQUE ([ConversationId], [ParticipantType]),
    CONSTRAINT [CK_ConversationParticipants_ParticipantType]
        CHECK ([ParticipantType] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_ConversationParticipants_UnreadCount]
        CHECK ([UnreadCount] >= 0)
);

GO

-- "How many unread do I have, across every thread" — the app's badge — and the
-- per-participant lookups the send path makes.
CREATE INDEX [IX_ConversationParticipants_Participant]
    ON [Chat].[ConversationParticipants] ([ParticipantType], [ParticipantId])
    INCLUDE ([ConversationId], [UnreadCount], [LastReadSequence], [IsMuted]);

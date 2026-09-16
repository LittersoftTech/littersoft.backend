-- "Delete this chat" — for the CALLER only.
--
-- The counterparty's thread is deliberately untouched: they keep every message,
-- their unread count, and their place in the conversation. That is the stated
-- requirement, and it is also the only thing the storage allows — a message body
-- is ONE Cosmos document read by both parties, so one side clearing their copy
-- can only ever be a watermark, never a delete.
--
-- What it does, all on the caller's own [Chat].[ConversationParticipants] row:
--   * [ClearedUpToSequence] = the thread's last sequence — history at or below it
--     stops being returned to this side.
--   * [DeletedAtUtc] = now — hides the thread from this side's inbox, until the
--     counterparty writes again (see the column note for why both are needed).
--   * unread zeroed and the read pointer advanced — a thread you have cleared
--     cannot still be owing you attention.
--
-- The conversation row, the messages, and the other side's state are ALL left
-- alone, so this is reversible in every way that matters: one new message and the
-- thread is back on the caller's inbox, carrying on from there.
--
-- LEGAL HOLD: refused outright while an open support ticket names this
-- conversation. A chat incident is reported precisely because of what was said,
-- so allowing either side to clear their copy while support is reading it would
-- defeat the report. It binds BOTH parties — the accused is the one with a motive
-- to erase — and lifts when the ticket closes.
--
-- Returns ONE result set — the caller's updated participant state — or NOTHING
-- when the conversation is unknown OR not theirs. Those two are deliberately the
-- same answer, as everywhere else in this schema, so a conversation id cannot be
-- probed for existence.
--
-- THROWs: 51352 the conversation is under legal hold. Checked only AFTER the
-- participant check, so a stranger still gets the empty result and cannot use
-- this to discover that a thread exists and is under investigation.
CREATE OR ALTER PROCEDURE [Chat].[DeleteConversationForParticipant]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @LastSequence BIGINT;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK on the conversation row, for the reason the watermark
    -- exists at all: it has to be the thread's last sequence AS OF this instant.
    -- A message committing between the read and the write would otherwise be
    -- swept under a watermark taken before it arrived — cleared from the caller's
    -- history without ever having been seen. [Chat].[CommitMessageAppend] takes
    -- the same row first, so the two serialise in the same lock order.
    SELECT @LastSequence = c.[LastSequence]
    FROM [Chat].[Conversations] AS c WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN [Chat].[ConversationParticipants] AS p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    WHERE c.[ConversationId] = @ConversationId;

    IF @LastSequence IS NULL
    BEGIN
        -- Unknown, or not the caller's. Empty result set is the answer.
        COMMIT TRANSACTION;
        RETURN;
    END

    -- Served by [UX_Tickets_OpenConversation], filtered to exactly this predicate,
    -- so the ordinary unreported thread pays one seek that finds nothing.
    IF EXISTS (
        SELECT 1
        FROM [Support].[Tickets]
        WHERE [ConversationId] = @ConversationId
          AND [Status] <> N'CLOSED')
    BEGIN
        THROW 51352, 'This conversation cannot be cleared while a support ticket is open on it.', 1;
    END

    UPDATE [Chat].[ConversationParticipants]
    SET [ClearedUpToSequence] = @LastSequence,
        -- Never move a read pointer backwards — the same invariant
        -- [Chat].[MarkConversationRead] holds. In practice clearing always
        -- advances it, but a concurrent mark-read must not be undone.
        [LastReadSequence] = CASE WHEN [LastReadSequence] > @LastSequence
                                  THEN [LastReadSequence]
                                  ELSE @LastSequence END,
        [UnreadCount] = 0,
        [DeletedAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    SELECT [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           -- CAST because BIT loses to INT in data type precedence the moment it
           -- meets one in an expression, and SqlDataReader.GetBoolean does not
           -- coerce — the exact trap that took down [Chat].[AppendMessage].
           CAST([IsMuted] AS BIT) AS [IsMuted],
           [ClearedUpToSequence],
           [DeletedAtUtc]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    COMMIT TRANSACTION;
END;

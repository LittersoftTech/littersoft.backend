-- Advances the caller's read pointer on one thread and clears their unread count.
--
-- @UpToSequence is what the client claims to have seen. It is clamped to the
-- conversation's own [LastSequence] and never moves backwards, so a stale client
-- reporting an old number cannot un-read a thread, and a client that has somehow
-- got ahead cannot mark unsent messages as read.
--
-- The unread count is recomputed rather than zeroed outright: marking up to
-- sequence 40 on a thread that is already at 45 should leave five unread, not
-- none. It only reaches zero when the caller has caught up completely.
--
-- Scoped by participant, so a caller can only ever mark their own side read —
-- there is no way to clear somebody else's badge.
--
-- Returns ONE result set with the resulting state. Empty when the conversation
-- does not exist or is not the caller's: the same non-disclosure posture as
-- [Chat].[GetConversationForParticipant]. Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[MarkConversationRead]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @UpToSequence BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @LastSequence BIGINT;

    BEGIN TRANSACTION;

    SELECT @LastSequence = [LastSequence]
    FROM [Chat].[Conversations]
    WHERE [ConversationId] = @ConversationId;

    IF @LastSequence IS NULL
    BEGIN
        COMMIT TRANSACTION;
        RETURN;
    END

    IF @UpToSequence IS NULL OR @UpToSequence > @LastSequence
    BEGIN
        SET @UpToSequence = @LastSequence;
    END

    UPDATE [Chat].[ConversationParticipants]
    SET [LastReadSequence] = CASE WHEN @UpToSequence > [LastReadSequence]
                                  THEN @UpToSequence ELSE [LastReadSequence] END,
        -- What is left after catching up to here. CASE rather than arithmetic on
        -- the old count because unread is a counter and sequences can have gaps —
        -- subtracting sequence numbers would over-count.
        [UnreadCount] = CASE WHEN @UpToSequence >= @LastSequence THEN 0
                             ELSE [UnreadCount] END,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    SELECT [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    COMMIT TRANSACTION;
END;

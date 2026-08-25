-- Phase 1 of sending a message: authorise the sender and assign the sequence.
--
-- It deliberately does NOTHING a client could observe. The inbox preview, the
-- recipient's unread count and their push all belong to phase 2
-- ([Chat].[CommitMessageAppend]), which runs only after the body is durable in
-- Cosmos. That split is the whole point: a send that fails at the Cosmos write
-- leaves no trace except a spent sequence number, instead of the old behaviour
-- where the thread advanced, the recipient was pushed, and the sender was told
-- the send had failed.
--
-- IDEMPOTENT ON @MessageId. The client mints it, and ([ConversationId],
-- [MessageId]) is the primary key of [Chat].[ConversationMessages] — so a retry,
-- whether it arrives over the hub or over REST, finds the existing reservation
-- and is handed the ORIGINAL sequence back with [IsReplay] = 1. A duplicate can
-- therefore never take a second sequence, and never produce a second message.
-- This does not depend on Cosmos, which matters because the case that needs
-- protecting most is the one where the Cosmos write is what failed.
--
-- The transaction commits before returning, so NO lock is held across the Cosmos
-- write that follows. Holding the conversation row — the row every send in the
-- thread serialises on — across an external network call would be the worst
-- possible place to put one.
--
-- Returns ONE result set: the sequence, the timestamp to stamp on the body, and
-- whether this is a replay of an already-reserved or already-committed message.
--
-- THROWs: 51323 blocked, 51325 conversation not found, 51326 sender is not a
-- party to it.
CREATE OR ALTER PROCEDURE [Chat].[ReserveMessageSequence]
    @ConversationId UNIQUEIDENTIFIER,
    @SenderType NVARCHAR(16),           -- 'Provider' | 'PetParent'
    @SenderId UNIQUEIDENTIFIER,
    -- The client-generated message id, which is also the Cosmos document id.
    @MessageId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @SenderType NOT IN (N'Provider', N'PetParent')
    BEGIN
        THROW 51326, 'You are not a party to this conversation.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @Sequence BIGINT = NULL;
    DECLARE @ReservedAtUtc DATETIME2(7) = NULL;
    DECLARE @CommittedAtUtc DATETIME2(7) = NULL;
    DECLARE @IsReplay BIT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK on the conversation row is what serialises concurrent sends and
    -- makes the sequence strictly increasing: two messages at once queue here
    -- rather than both reading the same LastSequence.
    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, ROWLOCK)
    WHERE [ConversationId] = @ConversationId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51325, 'Conversation was not found.', 1;
    END

    IF (@SenderType = N'Provider' AND @SenderId <> @ProviderId)
        OR (@SenderType = N'PetParent' AND @SenderId <> @PetParentId)
    BEGIN
        THROW 51326, 'You are not a party to this conversation.', 1;
    END

    -- Re-checked on every message, not just at conversation creation: a block
    -- raised mid-thread has to take effect immediately, and the conversation row
    -- already exists by then.
    IF EXISTS (
        SELECT 1
        FROM [Block].[BlockedParticipants]
        WHERE ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
               AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId)
           OR ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
               AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId))
    BEGIN
        THROW 51323, 'This conversation is not available.', 1;
    END

    -- The idempotency check. HOLDLOCK as well as UPDLOCK so the absence of a row
    -- is held too — without it two concurrent sends of the same @MessageId could
    -- both find nothing and both try to insert, and the loser would fail on the
    -- primary key instead of being told it is a replay.
    SELECT @Sequence = [Sequence],
           @ReservedAtUtc = [ReservedAtUtc],
           @CommittedAtUtc = [CommittedAtUtc],
           @IsReplay = 1
    FROM [Chat].[ConversationMessages] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId;

    IF @IsReplay = 0
    BEGIN
        SELECT @Sequence = [LastSequence] + 1
        FROM [Chat].[Conversations]
        WHERE [ConversationId] = @ConversationId;

        SET @ReservedAtUtc = @Now;

        -- LastSequence moves here, in phase 1, and is never rolled back if the
        -- body fails to land. It is a high-water mark, not a count — see the note
        -- on the column for why a gap costs nothing.
        UPDATE [Chat].[Conversations]
        SET [LastSequence] = @Sequence,
            [UpdatedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId;

        INSERT INTO [Chat].[ConversationMessages]
            ([ConversationId], [MessageId], [Sequence], [SenderType], [SenderId], [ReservedAtUtc])
        VALUES
            (@ConversationId, @MessageId, @Sequence, @SenderType, @SenderId, @ReservedAtUtc);
    END

    COMMIT TRANSACTION;

    -- Every flag is CAST to BIT explicitly. COALESCE(bit, 0) returns INT, because
    -- INT outranks BIT in data type precedence — which is exactly how the old
    -- [Chat].[AppendMessage] came to hand its caller an INT where a BIT was
    -- expected and crash every single send after the transaction had committed.
    SELECT @ConversationId AS [ConversationId],
           @MessageId AS [MessageId],
           @Sequence AS [Sequence],
           @ReservedAtUtc AS [CreatedAtUtc],
           CAST(@IsReplay AS BIT) AS [IsReplay],
           CAST(CASE WHEN @CommittedAtUtc IS NULL THEN 0 ELSE 1 END AS BIT) AS [IsCommitted];
END;

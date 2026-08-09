-- Records one chat message's SQL-side effects, in a single transaction: assign
-- its sequence number, refresh the inbox cache, move both participants' read
-- state, decide whether the recipient needs a push, and — when they do — queue it.
--
-- The message BODY is not stored here. It goes to Cosmos, partitioned by
-- conversation, which is where the volume belongs. This procedure owns everything
-- that has to be consistent and countable: ordering, unread counts, and the
-- newest-message cache the inbox list reads.
--
-- WHY THE PUSH DECISION LIVES HERE. It needs the recipient's presence, their mute
-- flag, and their device tokens — three reads against tables this transaction is
-- already in. Deciding it in C# would mean extra round trips and a window in
-- which presence could change between the decision and the write. Same reasoning
-- that put the notification enqueue inside the booking transition sprocs and the
-- pending-jobs check inside [Parent].[DeletePetParent].
--
-- ORDER OF WRITES, and the one trade-off in it. The caller writes to Cosmos AFTER
-- this returns, so a crash in between leaves a sequence number and a preview for a
-- message with no body. That is deliberate: the alternative (reserve, write,
-- commit) costs an extra SQL round trip on every single message. The client
-- retries with the same @MessageId, a fresh sequence is assigned, and the correct
-- preview overwrites the stale one. The burnt sequence is a harmless gap — see the
-- note on [Chat].[Conversations].[LastSequence] for why nothing counts them.
--
-- Returns THREE result sets:
--   1. the appended message's sequence + the conversation's new cache state
--   2. the recipient, and the notification id if one was queued (NULL if not)
--   3. the recipient's active FCM tokens (empty unless a notification was queued)
--
-- THROWs: 51323 blocked, 51325 conversation not found, 51326 sender is not a
-- party to it.
CREATE OR ALTER PROCEDURE [Chat].[AppendMessage]
    @ConversationId UNIQUEIDENTIFIER,
    @SenderType NVARCHAR(16),           -- 'Provider' | 'PetParent'
    @SenderId UNIQUEIDENTIFIER,
    -- The client-generated message id, which is also the Cosmos document id. It
    -- appears here only to key the notification's DedupeKey, so a retried send
    -- cannot produce a second push.
    @MessageId UNIQUEIDENTIFIER,
    -- What the inbox card shows. The caller truncates and, for an attachment,
    -- substitutes a label ("Photo") rather than sending a blob URL.
    @Preview NVARCHAR(200)
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
    DECLARE @Sequence BIGINT;

    DECLARE @RecipientType NVARCHAR(16) = CASE WHEN @SenderType = N'Provider'
                                               THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @RecipientId UNIQUEIDENTIFIER;
    DECLARE @RecipientUnread INT;
    DECLARE @RecipientIsMuted BIT;
    DECLARE @RecipientIsViewing BIT = 0;
    DECLARE @NotificationId UNIQUEIDENTIFIER = NULL;
    -- Returned to the caller so it can render the copy itself. The C#
    -- NotificationTemplateCatalog is the only place wording lives, and the
    -- renderer needs these parameters; without handing them back, the chat host
    -- would have to re-read the outbox row it just wrote.
    DECLARE @DataJson NVARCHAR(MAX) = NULL;

    BEGIN TRANSACTION;

    -- UPDLOCK on the conversation row is what serialises concurrent sends and
    -- makes the sequence strictly increasing: two messages at once queue here
    -- rather than both reading the same LastSequence.
    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @Sequence = [LastSequence] + 1
    FROM [Chat].[Conversations] WITH (UPDLOCK, ROWLOCK)
    WHERE [ConversationId] = @ConversationId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51325, 'Conversation was not found.', 1;
    END

    SET @RecipientId = CASE WHEN @RecipientType = N'Provider' THEN @ProviderId ELSE @PetParentId END;

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
        FROM [Chat].[BlockedParticipants]
        WHERE ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
               AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId)
           OR ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
               AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId))
    BEGIN
        THROW 51323, 'This conversation is not available.', 1;
    END

    UPDATE [Chat].[Conversations]
    SET [LastSequence] = @Sequence,
        [LastMessageAtUtc] = @Now,
        [LastMessagePreview] = @Preview,
        [LastMessageSenderType] = @SenderType,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId;

    -- Both sides in one statement, which is the reason participants are rows
    -- rather than column pairs on the conversation. The sender's read pointer
    -- advances because you have obviously read what you just sent.
    UPDATE [Chat].[ConversationParticipants]
    SET [LastReadSequence] = CASE WHEN [ParticipantType] = @SenderType
                                  THEN @Sequence ELSE [LastReadSequence] END,
        [UnreadCount] = CASE WHEN [ParticipantType] = @SenderType
                             THEN 0 ELSE [UnreadCount] + 1 END,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId;

    SELECT @RecipientUnread = [UnreadCount],
           @RecipientIsMuted = [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @RecipientType;

    -- Presence, at THREAD level rather than merely "are they connected". Someone
    -- connected but on another screen still deserves a push — they are not
    -- looking at this. Only an open thread suppresses it.
    IF EXISTS (
        SELECT 1
        FROM [Chat].[ChatConnections]
        WHERE [ParticipantType] = @RecipientType
          AND [ParticipantId] = @RecipientId
          AND [ActiveConversationId] = @ConversationId)
    BEGIN
        SET @RecipientIsViewing = 1;
    END

    IF @RecipientIsViewing = 0 AND COALESCE(@RecipientIsMuted, 0) = 0
    BEGIN
        -- Copy is NOT built here. The dispatcher's C# NotificationTemplateCatalog
        -- owns every user-facing string in the product, and chat is not an
        -- exception to that — this only supplies the type and its parameters.
        DECLARE @SenderName NVARCHAR(200) =
            CASE WHEN @SenderType = N'Provider'
                 THEN (SELECT LTRIM(RTRIM(COALESCE([FirstName], N'') + N' ' + COALESCE([LastName], N'')))
                       FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId)
                 ELSE (SELECT LTRIM(RTRIM(COALESCE([FirstName], N'') + N' ' + COALESCE([LastName], N'')))
                       FROM [Parent].[PetParents] WHERE [PetParentId] = @PetParentId)
            END;

        -- Joined live rather than denormalised onto the message, so a deleted
        -- account reads "Deleted Provider" / "Deleted User" — the same invariant
        -- the review and booking reads hold. NULLIF sends nothing at all when the
        -- name is blank, letting the renderer's "Someone" fallback take over.
        SET @DataJson =
        (
            SELECT
                -- --- the canonical id block ---
                N'MESSAGING'                                 AS [category],
                CAST(@ConversationId AS NVARCHAR(36))        AS [conversationId],
                CAST(@PetParentId AS NVARCHAR(36))           AS [parentId],
                CAST(@ProviderId AS NVARCHAR(36))            AS [providerId],
                -- --- template parameters ---
                NULLIF(@SenderName, N'')                     AS [senderName]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        DECLARE @DedupeKey NVARCHAR(200) =
            N'MESSAGE_RECEIVED:' + CAST(@MessageId AS NVARCHAR(36));

        EXEC [Notification].[EnqueueInstantNotification]
            @Audience = @RecipientType,
            @RecipientId = @RecipientId,
            @NotificationType = N'MESSAGE_RECEIVED',
            @EntityType = N'Conversation',
            @EntityId = @ConversationId,
            @DataJson = @DataJson,
            -- Keyed on the message, not the conversation: every message is a
            -- distinct event, but a retried send of the SAME message must not
            -- buzz twice. Prefixed and cast explicitly, matching the DedupeKey
            -- shape [Notification].[EnqueueBookingNotification] builds.
            @DedupeKey = @DedupeKey,
            @SuppressResultSet = 1,
            @NotificationId = @NotificationId OUTPUT;
    END

    -- Result set 1 — what the sender gets back.
    SELECT @ConversationId AS [ConversationId],
           @Sequence AS [Sequence],
           @Now AS [CreatedAtUtc],
           @Preview AS [LastMessagePreview];

    -- Result set 2 — who to deliver to, and whether a push was queued.
    SELECT @RecipientType AS [RecipientType],
           @RecipientId AS [RecipientId],
           COALESCE(@RecipientUnread, 0) AS [RecipientUnreadCount],
           @RecipientIsViewing AS [RecipientIsViewing],
           COALESCE(@RecipientIsMuted, 0) AS [RecipientIsMuted],
           @NotificationId AS [NotificationId],
           @DataJson AS [DataJson];

    -- Result set 3 — the devices to push to. Empty when no notification was
    -- queued, so the caller can branch on row count alone.
    IF @NotificationId IS NULL
    BEGIN
        SELECT CAST(NULL AS NVARCHAR(2048)) AS [FcmToken],
               CAST(NULL AS NVARCHAR(32)) AS [DevicePlatform]
        WHERE 1 = 0;
    END
    ELSE IF @RecipientType = N'Provider'
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @RecipientId
          AND [IsActive] = 1;
    END
    ELSE
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @RecipientId
          AND [IsActive] = 1;
    END

    COMMIT TRANSACTION;
END;

-- Phase 2 of sending a message, run only once the body is durable in Cosmos.
--
-- Everything a client can observe happens here and nowhere else: the inbox cache,
-- both sides' read state, and the recipient's push. That is what makes a send
-- atomic in the only sense that matters to a user — either the message exists and
-- is delivered, or nothing about the thread changed. Phase 1
-- ([Chat].[ReserveMessageSequence]) deliberately leaves no observable trace, so
-- there is nothing to undo when the body fails to land.
--
-- WHY THE PUSH DECISION LIVES HERE. It needs the recipient's presence, their mute
-- flag, and their device tokens — three reads against tables this transaction is
-- already in. Deciding it in C# would mean extra round trips and a window in
-- which presence could change between the decision and the write. Same reasoning
-- that put the notification enqueue inside the booking transition sprocs and the
-- pending-jobs check inside [Parent].[DeletePetParent].
--
-- IDEMPOTENT. A second commit of the same message is a no-op: it moves no counter
-- and queues no second push. That is what lets the caller retry safely after a
-- failure between the Cosmos write and this call — and it is load-bearing, since
-- such a retry is the only thing that repairs a message whose body landed but
-- whose delivery did not.
--
-- OUT-OF-ORDER COMMITS ARE EXPECTED. Two sends race, the later one's body lands
-- first, and it commits first. The unread count must still count both, but the
-- inbox preview must show the NEWER message — so the counter update is
-- unconditional while the cache update is guarded on this still being the highest
-- committed sequence.
--
-- Returns TWO result sets:
--   1. the recipient, the message's sequence/timestamp, and the notification id if
--      one was queued (NULL if not)
--   2. the recipient's active FCM tokens (empty unless a notification was queued)
--
-- THROWs: 51325 conversation not found, 51328 no reservation for this message.
CREATE OR ALTER PROCEDURE [Chat].[CommitMessageAppend]
    @ConversationId UNIQUEIDENTIFIER,
    -- The message reserved by phase 1. The sender is NOT a parameter: it is read
    -- back from the reservation, so the two phases cannot disagree about who sent
    -- this.
    @MessageId UNIQUEIDENTIFIER,
    -- What the inbox card shows. The caller truncates and, for an attachment,
    -- substitutes a label ("Photo") rather than sending a blob URL.
    @Preview NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    DECLARE @Sequence BIGINT = NULL;
    DECLARE @SenderType NVARCHAR(16) = NULL;
    DECLARE @ReservedAtUtc DATETIME2(7) = NULL;
    DECLARE @AlreadyCommitted BIT = 0;

    DECLARE @RecipientType NVARCHAR(16);
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

    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, ROWLOCK)
    WHERE [ConversationId] = @ConversationId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51325, 'Conversation was not found.', 1;
    END

    SELECT @Sequence = [Sequence],
           @SenderType = [SenderType],
           @ReservedAtUtc = [ReservedAtUtc],
           @AlreadyCommitted = CASE WHEN [CommittedAtUtc] IS NULL THEN 0 ELSE 1 END
    FROM [Chat].[ConversationMessages] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId;

    IF @Sequence IS NULL
    BEGIN
        -- Phase 2 without phase 1. Not reachable through the service, which always
        -- reserves first; surfacing it beats silently inventing a sequence.
        THROW 51328, 'No reservation exists for this message.', 1;
    END

    SET @RecipientType = CASE WHEN @SenderType = N'Provider'
                              THEN N'PetParent' ELSE N'Provider' END;
    SET @RecipientId = CASE WHEN @RecipientType = N'Provider'
                            THEN @ProviderId ELSE @PetParentId END;

    IF @AlreadyCommitted = 0
    BEGIN
        UPDATE [Chat].[ConversationMessages]
        SET [CommittedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId
          AND [MessageId] = @MessageId;

        -- The inbox cache, but only if nothing newer has already committed — see
        -- the out-of-order note above. Seeks [UQ_ConversationMessages_Sequence].
        IF NOT EXISTS (
            SELECT 1
            FROM [Chat].[ConversationMessages]
            WHERE [ConversationId] = @ConversationId
              AND [CommittedAtUtc] IS NOT NULL
              AND [Sequence] > @Sequence)
        BEGIN
            UPDATE [Chat].[Conversations]
            SET [LastMessageAtUtc] = @ReservedAtUtc,
                [LastMessagePreview] = @Preview,
                [LastMessageSenderType] = @SenderType,
                [UpdatedAtUtc] = @Now
            WHERE [ConversationId] = @ConversationId;
        END

        -- Both sides in one statement, which is the reason participants are rows
        -- rather than column pairs on the conversation. The sender's read pointer
        -- advances because you have obviously read what you just sent — but only
        -- forwards, so an out-of-order commit cannot drag it back.
        UPDATE [Chat].[ConversationParticipants]
        SET [LastReadSequence] = CASE WHEN [ParticipantType] = @SenderType
                                           AND @Sequence > [LastReadSequence]
                                      THEN @Sequence ELSE [LastReadSequence] END,
            [UnreadCount] = CASE WHEN [ParticipantType] = @SenderType
                                 THEN 0 ELSE [UnreadCount] + 1 END,
            [UpdatedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId;
    END

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

    -- @AlreadyCommitted guards the enqueue as well as the counters: a replay must
    -- not buzz the recipient a second time for one message.
    IF @AlreadyCommitted = 0 AND @RecipientIsViewing = 0 AND COALESCE(@RecipientIsMuted, 0) = 0
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

    -- Result set 1 — who to deliver to, and whether a push was queued.
    --
    -- EVERY BIT COLUMN IS CAST EXPLICITLY. COALESCE(bit, 0) returns INT, because
    -- INT outranks BIT in data type precedence, and the C# reader calls
    -- GetBoolean on this ordinal. The predecessor of this procedure got that
    -- wrong on [RecipientIsMuted] and threw InvalidCastException on every send,
    -- AFTER its transaction had committed — the message was delivered and the
    -- sender was told it had failed. Do not drop the CASTs.
    SELECT @RecipientType AS [RecipientType],
           @RecipientId AS [RecipientId],
           COALESCE(@RecipientUnread, 0) AS [RecipientUnreadCount],
           CAST(@RecipientIsViewing AS BIT) AS [RecipientIsViewing],
           CAST(COALESCE(@RecipientIsMuted, 0) AS BIT) AS [RecipientIsMuted],
           @NotificationId AS [NotificationId],
           @DataJson AS [DataJson],
           @Sequence AS [Sequence],
           @ReservedAtUtc AS [CreatedAtUtc];

    -- Result set 2 — the devices to push to. Empty when no notification was
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

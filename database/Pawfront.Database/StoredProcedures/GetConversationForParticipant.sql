-- The authorisation point read: "does this conversation exist, and is the caller
-- one of its two participants?"
--
-- Every path that touches a thread goes through this first — the history read,
-- the hub's JoinConversation (so a client cannot subscribe to a group by
-- guessing its name), mark-read, and attachment upload. It returns NOTHING for a
-- conversation the caller is not part of, deliberately conflating "no such
-- thread" with "not yours" so the caller answers 404 for both and an id cannot be
-- probed for existence. Same posture as [Provider].[DeactivateProviderDeviceToken]
-- and the review-photo reads.
--
-- Returns TWO result sets: the conversation with the caller's own participant
-- state, then the counterparty's name joined LIVE from
-- [Provider].[Providers] / [Parent].[PetParents] — never denormalised, so a
-- deleted account reads "Deleted Provider" / "Deleted User" rather than keeping
-- its real name frozen in the thread.
--
-- Never THROWs; an empty result is the answer.
CREATE OR ALTER PROCEDURE [Chat].[GetConversationForParticipant]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.[ConversationId],
           c.[ProviderId],
           c.[PetParentId],
           c.[LastSequence],
           c.[LastMessageAtUtc],
           c.[LastMessagePreview],
           c.[LastMessageSenderType],
           c.[CreatedAtUtc],
           c.[UpdatedAtUtc],
           p.[LastReadSequence],
           p.[UnreadCount],
           CAST(p.[IsMuted] AS BIT) AS [IsMuted],
           -- The caller's "delete chat" watermark, appended LAST so the existing
           -- ordinals stay put. The history read needs it: message bodies are
           -- shared with the counterparty, so hiding a cleared thread's messages
           -- can only be done by filtering below this sequence on the way out.
           -- 0 on a thread that was never cleared, which filters nothing.
           p.[ClearedUpToSequence],
           p.[DeletedAtUtc]
    FROM [Chat].[Conversations] c
    INNER JOIN [Chat].[ConversationParticipants] p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    WHERE c.[ConversationId] = @ConversationId;

    -- The other side, for the thread header. Scoped by the same participant join
    -- as above so a non-participant gets nothing here either.
    SELECT CASE WHEN @ParticipantType = N'Provider' THEN N'PetParent' ELSE N'Provider' END
               AS [CounterpartyType],
           CASE WHEN @ParticipantType = N'Provider' THEN c.[PetParentId] ELSE c.[ProviderId] END
               AS [CounterpartyId],
           CASE WHEN @ParticipantType = N'Provider'
                THEN LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
                ELSE LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
           END AS [CounterpartyName],
           -- Only the pet parent has a photo on their SQL row. A provider's image
           -- lives in their Cosmos offering document, so the caller resolves that
           -- one separately — the same split GetBookingDetail's providerPhotoUrl
           -- has to live with.
           CASE WHEN @ParticipantType = N'Provider' THEN pp.[ProfilePhotoUrl] END
               AS [CounterpartyPhotoUrl],
           CASE WHEN @ParticipantType = N'Provider' THEN pp.[IsDeleted] ELSE pr.[IsDeleted] END
               AS [CounterpartyIsDeleted],
           -- The provider's Cosmos partition key, so the caller can point-read the
           -- offering document their image lives in. Appended LAST so the existing
           -- ordinals the reader uses stay put. See Chat.ListConversations for the
           -- full note.
           CASE WHEN @ParticipantType = N'PetParent' THEN reg.[ServiceCategory] END
               AS [CounterpartyServiceCategory],
           -- A block closes the thread; the flag lets the app disable the
           -- composer rather than let a send fail. TRUE in EITHER direction.
           -- Appended LAST, like the category above, so the reader's existing
           -- ordinals stay put.
           CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM [Block].[BlockedParticipants] bp
                    WHERE (bp.[BlockerType] = N'Provider' AND bp.[BlockerId] = c.[ProviderId]
                           AND bp.[BlockedType] = N'PetParent' AND bp.[BlockedId] = c.[PetParentId])
                       OR (bp.[BlockerType] = N'PetParent' AND bp.[BlockerId] = c.[PetParentId]
                           AND bp.[BlockedType] = N'Provider' AND bp.[BlockedId] = c.[ProviderId])
                ) THEN 1 ELSE 0 END AS BIT) AS [IsBlocked],
           -- Only the caller's OWN block offers an Unblock button; one placed
           -- against them is never named. See [Block].[ListMyBlockedCounterparties].
           CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM [Block].[BlockedParticipants] bp
                    WHERE bp.[BlockerType] = @ParticipantType
                      AND bp.[BlockerId] = @ParticipantId
                      AND bp.[BlockedId] = CASE WHEN @ParticipantType = N'Provider'
                                                THEN c.[PetParentId] ELSE c.[ProviderId] END
                ) THEN 1 ELSE 0 END AS BIT) AS [BlockedByMe]
    FROM [Chat].[Conversations] c
    INNER JOIN [Chat].[ConversationParticipants] p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    LEFT JOIN [Provider].[Providers] pr ON pr.[ProviderId] = c.[ProviderId]
    LEFT JOIN [Parent].[PetParents] pp ON pp.[PetParentId] = c.[PetParentId]
    LEFT JOIN [Provider].[ProviderServiceRegistrations] reg ON reg.[ProviderId] = c.[ProviderId]
    WHERE c.[ConversationId] = @ConversationId;
END;

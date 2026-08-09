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
           p.[IsMuted]
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
               AS [CounterpartyIsDeleted]
    FROM [Chat].[Conversations] c
    INNER JOIN [Chat].[ConversationParticipants] p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    LEFT JOIN [Provider].[Providers] pr ON pr.[ProviderId] = c.[ProviderId]
    LEFT JOIN [Parent].[PetParents] pp ON pp.[PetParentId] = c.[PetParentId]
    WHERE c.[ConversationId] = @ConversationId;
END;

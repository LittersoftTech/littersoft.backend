-- Records which thread a connection currently has open — or NULL when the client
-- has navigated away.
--
-- This one column is the whole of presence-gated push: [Chat].[AppendMessage]
-- sends an FCM push only when the recipient has no connection whose
-- [ActiveConversationId] matches. Without it the choice would be to buzz someone
-- for a message they are reading, or to leave an offline recipient silent.
--
-- It is set from JoinConversation / LeaveConversation on the hub, and cleared
-- implicitly when the connection row is deleted.
--
-- Scoped by participant as well as connection id: a connection may only declare
-- itself viewing on behalf of the participant it authenticated as, so a client
-- cannot suppress somebody else's push by claiming their id.
--
-- The caller is expected to have already authorised the participant against the
-- conversation (via [Chat].[GetConversationForParticipant]); this does not
-- re-check membership, because being "on" a thread you are not part of has no
-- effect — the presence match in AppendMessage is scoped to the recipient.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[SetActiveConversation]
    @ConnectionId NVARCHAR(128),
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @ConversationId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [Chat].[ChatConnections]
    SET [ActiveConversationId] = @ConversationId,
        -- Opening a thread is proof of life, so it counts as a heartbeat and
        -- keeps the stale sweep off an actively used connection.
        [LastHeartbeatAtUtc] = SYSUTCDATETIME()
    WHERE [ConnectionId] = @ConnectionId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;
END;

-- The app's chat badge: how many unread messages the caller has, and across how
-- many threads.
--
-- Both numbers, because they answer different questions — the tab badge wants
-- the message total, while "3 conversations need you" is the more useful line in
-- a notification summary. Computing them separately client-side would need the
-- whole conversation list.
--
-- Served entirely from [IX_ConversationParticipants_Participant], which INCLUDEs
-- [UnreadCount], so it never touches [Chat].[Conversations].
--
-- Returns ONE result set, always exactly one row (zeros when the caller has no
-- conversations). Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[GetUnreadSummary]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- A muted thread still counts toward the badge. Muting silences the push, it
    -- does not mark the messages read — the user still has them waiting.
    SELECT COALESCE(SUM([UnreadCount]), 0) AS [UnreadMessageCount],
           COALESCE(SUM(CASE WHEN [UnreadCount] > 0 THEN 1 ELSE 0 END), 0)
               AS [UnreadConversationCount]
    FROM [Chat].[ConversationParticipants]
    WHERE [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;
END;

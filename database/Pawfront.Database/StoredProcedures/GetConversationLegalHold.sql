-- Is this conversation under legal hold, and if so which ticket holds it?
--
-- Exists because ONE of the two chat delete paths cannot check the hold for
-- itself. Clearing a thread is a T-SQL write
-- ([Chat].[DeleteConversationForParticipant]) and consults [Support].[Tickets]
-- inline. Deleting a MESSAGE is not: the body lives in Cosmos and the retraction
-- is a document replace, with no SQL statement in the path to hang the check on.
-- So the service reads this first.
--
-- Returns ONE result set, empty when the thread is free. A row means held, and
-- carries the ticket so the refusal can name it — "TK-000123 is open on this
-- chat" is actionable in a way that "you cannot delete this" is not.
--
-- Both parties are held, not just the reporter. The accused is the one with a
-- motive to erase, so a hold binding only the person who raised it would be
-- decorative.
--
-- Never THROWs.
CREATE OR ALTER PROCEDURE [Support].[GetConversationLegalHold]
    @ConversationId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Served by [UX_Tickets_OpenConversation], which is filtered to exactly this
    -- predicate: on the overwhelmingly common free-thread path this is an index
    -- seek that finds nothing.
    SELECT TOP (1)
           [TicketId],
           [TicketNumber],
           [TicketType],
           [Status]
    FROM [Support].[Tickets]
    WHERE [ConversationId] = @ConversationId
      AND [Status] <> N'CLOSED'
    ORDER BY [CreatedAtUtc] ASC;
END;

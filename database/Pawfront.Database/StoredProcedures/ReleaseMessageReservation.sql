-- Undoes phase 1 when the Cosmos write between the two phases fails.
--
-- Phase 1 deliberately changes nothing observable, so this is not a compensating
-- transaction in the usual sense — there is no preview to restore, no unread
-- count to decrement and no push to recall. All it does is free the
-- ([ConversationId], [MessageId]) key so a retry gets a clean reservation rather
-- than inheriting a sequence from a send that never happened.
--
-- BEST EFFORT, and the caller treats a failure here as a log line. If the row is
-- left behind, a retry of the same @MessageId simply finds it uncommitted, reuses
-- its sequence and completes the send — which is the correct outcome anyway. The
-- only cost of never calling this is an orphaned row and a gap in the sequence,
-- and gaps are already free (see [Chat].[Conversations].[LastSequence]).
--
-- [LastSequence] is NOT rewound. Rewinding it would race with any send that
-- reserved after this one and hand two messages the same number.
--
-- A COMMITTED reservation is never deleted: that row describes a real message
-- whose body is in Cosmos, and removing it would strand the body and let a
-- replay of the same id take a second sequence.
--
-- Never THROWs. An unknown message id is simply nothing to release.
CREATE OR ALTER PROCEDURE [Chat].[ReleaseMessageReservation]
    @ConversationId UNIQUEIDENTIFIER,
    @MessageId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DELETE FROM [Chat].[ConversationMessages]
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId
      AND [CommittedAtUtc] IS NULL;

    SELECT CAST(CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END AS BIT) AS [WasReleased];
END;

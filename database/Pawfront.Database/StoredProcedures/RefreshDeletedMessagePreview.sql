-- Rewrites a thread's inbox preview after its newest message has been retracted.
--
-- WHY THIS EXISTS. [Chat].[Conversations] carries a denormalised copy of the last
-- message — preview, timestamp, sender — so the inbox renders in one indexed read
-- instead of a Cosmos query per thread. A soft delete clears the message's content
-- in Cosmos and never touches SQL, so without this the retracted text goes on
-- being shown on the conversation card until something else is sent. That was a
-- reported bug: the message was gone from the thread and still legible in the
-- inbox.
--
-- The message itself is deliberately NOT removed (see the store's SoftDeleteAsync):
-- it keeps its place and its sequence, so it is still the thread's last activity
-- and the card keeps its own LastMessageAtUtc. Only the preview line changes, to
-- whatever placeholder the caller composes — user-facing copy lives in C#
-- (ChatLimits.DeletedPreviewLabel), as it does for the "Photo" label.
--
-- @Sequence IS A GUARD. The update applies only while that sequence is still the
-- thread's last, which makes two things right at once:
--   * a message that landed between the delete and this call has already moved the
--     preview on, and must not be dragged back to a retraction notice;
--   * deleting an OLDER message no-ops, which is correct — it was never on the card.
-- Reading and writing in one statement means no lock is held across a round trip
-- and no separate existence check can go stale underneath it.
--
-- Idempotent: a repeated delete writes the same placeholder over itself. Returns
-- nothing; the caller does not branch on the outcome.
CREATE OR ALTER PROCEDURE [Chat].[RefreshDeletedMessagePreview]
    @ConversationId UNIQUEIDENTIFIER,
    @Sequence BIGINT,
    @Preview NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [Chat].[Conversations]
    SET [LastMessagePreview] = @Preview,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ConversationId] = @ConversationId
      AND [LastSequence] = @Sequence;
END;

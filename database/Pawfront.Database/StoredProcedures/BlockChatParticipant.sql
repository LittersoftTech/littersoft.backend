-- Blocks the counterparty, in the caller's direction.
--
-- Idempotent: blocking somebody already blocked returns the existing row rather
-- than failing or duplicating, so a double-tap is harmless.
--
-- A block stops new messages BOTH ways and stops the thread being reopened
-- ([Chat].[GetOrCreateConversation] and [Chat].[ReserveMessageSequence] both consult this
-- table, in either direction). It deliberately leaves existing history in place:
-- what was already said is part of both parties' record, and deleting it would
-- also destroy what a blocked user might need in order to report the exchange.
--
-- Returns ONE result set: the block row.
--
-- THROWs: 51327 the two parties are on the same side (a conversation only ever
-- runs provider <-> parent, so such a block could never be consulted).
CREATE OR ALTER PROCEDURE [Chat].[BlockChatParticipant]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @BlockedType NVARCHAR(16),
    @BlockedId UNIQUEIDENTIFIER,
    @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BlockerType NOT IN (N'Provider', N'PetParent')
        OR @BlockedType NOT IN (N'Provider', N'PetParent')
        OR @BlockerType = @BlockedType
    BEGIN
        THROW 51327, 'A chat block must run between a provider and a pet parent.', 1;
    END

    IF LTRIM(RTRIM(COALESCE(@Reason, N''))) = N''
    BEGIN
        SET @Reason = NULL;
    END

    DECLARE @ChatBlockId UNIQUEIDENTIFIER;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK over the unique 4-tuple: with no row yet this takes a
    -- range lock, so two concurrent blocks serialise instead of one hitting a
    -- UNIQUE violation.
    SELECT @ChatBlockId = [ChatBlockId]
    FROM [Chat].[BlockedParticipants] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId
      AND [BlockedType] = @BlockedType
      AND [BlockedId] = @BlockedId;

    IF @ChatBlockId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([ChatBlockId] UNIQUEIDENTIFIER);

        INSERT INTO [Chat].[BlockedParticipants]
            ([BlockerType], [BlockerId], [BlockedType], [BlockedId], [Reason], [CreatedAtUtc])
        OUTPUT inserted.[ChatBlockId] INTO @Inserted
        VALUES
            (@BlockerType, @BlockerId, @BlockedType, @BlockedId, @Reason, @Now);

        SELECT @ChatBlockId = [ChatBlockId] FROM @Inserted;
    END

    SELECT [ChatBlockId],
           [BlockerType],
           [BlockerId],
           [BlockedType],
           [BlockedId],
           [Reason],
           [CreatedAtUtc]
    FROM [Chat].[BlockedParticipants]
    WHERE [ChatBlockId] = @ChatBlockId;

    COMMIT TRANSACTION;
END;

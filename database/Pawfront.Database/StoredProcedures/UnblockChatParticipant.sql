-- Lifts a block the caller placed.
--
-- Scoped to the caller as BLOCKER, so a user can only ever undo their own block —
-- there is no way to remove one placed against you.
--
-- Returns ONE result set describing what was lifted. Empty when the id is unknown
-- OR belongs to somebody else's block: deliberately the same case, so a block id
-- cannot be probed for existence (the posture
-- [Provider].[DeactivateProviderDeviceToken] and the review-photo delete take).
-- The caller maps an empty result to 404. Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[UnblockChatParticipant]
    @ChatBlockId UNIQUEIDENTIFIER,
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [Chat].[BlockedParticipants]
    OUTPUT deleted.[ChatBlockId],
           deleted.[BlockerType],
           deleted.[BlockerId],
           deleted.[BlockedType],
           deleted.[BlockedId],
           deleted.[Reason],
           deleted.[CreatedAtUtc]
    WHERE [ChatBlockId] = @ChatBlockId
      AND [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId;
END;

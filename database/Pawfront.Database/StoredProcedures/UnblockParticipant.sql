-- Lifts a block the caller placed.
--
-- Scoped to the caller as BLOCKER, so a user can only ever undo their own block --
-- there is no way to remove one placed against you.
--
-- A block is ALWAYS the blocker's to lift, with no exceptions. A support ticket
-- does not freeze one: reporting somebody does not block them, so the two are
-- independent remedies -- the user decides who they will deal with, support
-- decides what to do about the report.
--
-- UNBLOCKING RESTORES CONTACT, NOT BOOKINGS. The pair can message, book, see each
-- other's events and find each other in search again from the moment the row goes.
-- The jobs the block cancelled stay cancelled: they were cancelled through the
-- ordinary transition, with an audit row and their capacity released back to the
-- provider's calendar, and that capacity may since have been sold to somebody
-- else. Re-booking is a new booking.
--
-- Returns ONE result set describing what was lifted. Empty when the id is unknown
-- OR belongs to somebody else's block: deliberately the same case, so a block id
-- cannot be probed for existence (the posture
-- [Provider].[DeactivateProviderDeviceToken] and the review-photo delete take).
-- The caller maps an empty result to 404.
CREATE OR ALTER PROCEDURE [Block].[UnblockParticipant]
    @BlockId UNIQUEIDENTIFIER,
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DELETE FROM [Block].[BlockedParticipants]
    OUTPUT deleted.[BlockId],
           deleted.[BlockerType],
           deleted.[BlockerId],
           deleted.[BlockedType],
           deleted.[BlockedId],
           deleted.[Reason],
           deleted.[CreatedAtUtc]
    WHERE [BlockId] = @BlockId
      AND [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId;
END;

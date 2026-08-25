-- Every counterparty the caller is blocked from, in EITHER direction.
--
-- This is the single read behind all the per-request block state: the isBlocked
-- flag on booking cards and booking details, the flag on conversation reads, and
-- the filter that removes a blocked provider from browse and the five searches.
-- It is scoped to the ACTOR rather than taking a list of counterparty ids so a
-- page of twenty bookings costs ONE round trip instead of twenty -- the same
-- reasoning behind [Review].[ListPetParentBookingReviews] and
-- [Support].[ListMyOpenTicketSubjects].
--
-- BOTH DIRECTIONS, because every rule this feeds is symmetric: a booking is
-- refused, a thread is closed and an event is hidden whichever party placed the
-- block. That is also why the two directions collapse to ONE row per
-- counterparty -- a mutual block is still one severed relationship, and two rows
-- would make every caller de-duplicate.
--
-- [BlockedByMe] is what the apps branch on for the ACTION: only my own block
-- offers an Unblock button, so [BlockId] is returned ONLY for my own. Handing
-- back the id of a block placed against me would both be useless -- I cannot lift
-- it -- and leak that the other party acted, which is the one thing a block must
-- not confirm. A caller seeing isBlocked with BlockedByMe false shows a neutral
-- "not available".
--
-- Returns ONE result set, one row per blocked counterparty. Never THROWs.
CREATE OR ALTER PROCEDURE [Block].[ListMyBlockedCounterparties]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [CounterpartyType],
           [CounterpartyId],
           -- CAST: MAX over a CASE yielding INT literals is INT, and
           -- SqlDataReader.GetBoolean does not coerce -- it throws.
           CAST(MAX([IsMine]) AS BIT) AS [BlockedByMe],
           MAX([MyBlockId]) AS [BlockId],
           MIN([CreatedAtUtc]) AS [BlockedAtUtc]
    FROM (
        -- Blocks I placed.
        SELECT b.[BlockedType] AS [CounterpartyType],
               b.[BlockedId] AS [CounterpartyId],
               1 AS [IsMine],
               b.[BlockId] AS [MyBlockId],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Block].[BlockedParticipants] b
        WHERE b.[BlockerType] = @ParticipantType
          AND b.[BlockerId] = @ParticipantId

        UNION ALL

        -- Blocks placed against me. The id is deliberately NOT carried through.
        SELECT b.[BlockerType] AS [CounterpartyType],
               b.[BlockerId] AS [CounterpartyId],
               0 AS [IsMine],
               NULL AS [MyBlockId],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Block].[BlockedParticipants] b
        WHERE b.[BlockedType] = @ParticipantType
          AND b.[BlockedId] = @ParticipantId
    ) AS [Blocks]
    GROUP BY [CounterpartyType], [CounterpartyId];
END;

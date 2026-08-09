-- Everyone the caller has blocked, newest first — the "blocked users" settings
-- screen.
--
-- Lists only blocks the caller PLACED. Blocks placed against them are
-- deliberately not returned: telling someone they have been blocked confirms the
-- other party acted, which is the thing a block is meant to end. A blocked sender
-- gets the same neutral "this conversation is not available" either way.
--
-- Names joined LIVE, so a blocked account that has since been deleted reads
-- "Deleted Provider" / "Deleted User" rather than keeping its real name — the
-- same invariant every other counterparty read here holds.
--
-- Returns ONE result set. Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[ListBlockedParticipants]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT b.[ChatBlockId],
           b.[BlockerType],
           b.[BlockerId],
           b.[BlockedType],
           b.[BlockedId],
           b.[Reason],
           b.[CreatedAtUtc],
           CASE WHEN b.[BlockedType] = N'Provider'
                THEN LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
                ELSE LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
           END AS [BlockedName],
           -- Parent photos only; a provider's image lives in their Cosmos
           -- offering document and is resolved by the caller if wanted.
           CASE WHEN b.[BlockedType] = N'PetParent' THEN pp.[ProfilePhotoUrl] END
               AS [BlockedPhotoUrl]
    FROM [Chat].[BlockedParticipants] b
    LEFT JOIN [Provider].[Providers] pr
        ON b.[BlockedType] = N'Provider' AND pr.[ProviderId] = b.[BlockedId]
    LEFT JOIN [Parent].[PetParents] pp
        ON b.[BlockedType] = N'PetParent' AND pp.[PetParentId] = b.[BlockedId]
    WHERE b.[BlockerType] = @BlockerType
      AND b.[BlockerId] = @BlockerId
    ORDER BY b.[CreatedAtUtc] DESC, b.[ChatBlockId] DESC;
END;

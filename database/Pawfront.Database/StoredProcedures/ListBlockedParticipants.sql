-- Everyone the caller has blocked, newest first -- the "blocked list" screen.
--
-- Lists only blocks the caller PLACED. Blocks placed against them are
-- deliberately not returned: telling someone they have been blocked confirms the
-- other party acted, which is the thing a block is meant to end. A blocked user
-- gets the same neutral "not available" wherever they run into it.
--
-- Names joined LIVE, so a blocked account that has since been deleted reads
-- "Deleted Provider" / "Deleted User" rather than keeping its real name -- the
-- same invariant every other counterparty read here holds.
--
-- [BlockedServiceCategory] is NOT for display. It is the Cosmos partition key the
-- caller needs in order to fetch a blocked PROVIDER's BUSINESS name and image
-- from their offering document: [Provider].[Providers] holds only the person's
-- own name, and a parent who blocked "Happy Paws Hotel" must see that, not just
-- the owner's. One point read per distinct provider on the page, best-effort --
-- a provider still mid-onboarding has no offering yet, and the list must still
-- render. Null for a blocked pet parent, who has no business.
--
-- Paged with [TotalCount] via COUNT(*) OVER(), so one round trip serves the page
-- and its total. @Take is clamped to 20, the figure every other list here uses.
--
-- Returns ONE result set. Never THROWs.
CREATE OR ALTER PROCEDURE [Block].[ListBlockedParticipants]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;
    IF @Take > 20 SET @Take = 20;

    SELECT b.[BlockId],
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
           -- offering document and is resolved by the caller alongside the
           -- business name, using the category below.
           CASE WHEN b.[BlockedType] = N'PetParent' THEN pp.[ProfilePhotoUrl] END
               AS [BlockedPhotoUrl],
           -- UNIQUE on ProviderId, so this join cannot fan the page out.
           CASE WHEN b.[BlockedType] = N'Provider' THEN reg.[ServiceCategory] END
               AS [BlockedServiceCategory],
           COUNT(*) OVER() AS [TotalCount]
    FROM [Block].[BlockedParticipants] b
    LEFT JOIN [Provider].[Providers] pr
        ON b.[BlockedType] = N'Provider' AND pr.[ProviderId] = b.[BlockedId]
    LEFT JOIN [Parent].[PetParents] pp
        ON b.[BlockedType] = N'PetParent' AND pp.[PetParentId] = b.[BlockedId]
    LEFT JOIN [Provider].[ProviderServiceRegistrations] reg
        ON b.[BlockedType] = N'Provider' AND reg.[ProviderId] = b.[BlockedId]
    WHERE b.[BlockerType] = @BlockerType
      AND b.[BlockerId] = @BlockerId
    ORDER BY b.[CreatedAtUtc] DESC, b.[BlockId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

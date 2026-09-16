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
-- [LastMessagePreview] is the pair's newest message, read from the SAME
-- denormalised cache the chat inbox card renders -- so a blocked list and an
-- inbox can never show two different "last messages" for one pair. NULL when the
-- two have never spoken, and NULL when the CALLER has cleared the thread and
-- nothing has been said since: "delete chat" must not leak the text it cleared
-- onto another screen, so this repeats the inbox's own predicate rather than
-- reading around it. The counterparty's copy is irrelevant here and is not read.
--
-- Paged with [TotalCount] via COUNT(*) OVER(), so one round trip serves the page
-- and its total. @Take is clamped to 20, the figure every other list here uses.
--
-- Returns ONE result set. Never THROWs.
CREATE OR ALTER PROCEDURE [Block].[ListBlockedParticipants]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20,
    -- Free text from the blocked list's search bar. NULL or blank returns the
    -- ordinary page; see the paging note below for what a term actually does
    -- here, which is not what a search parameter usually does.
    @Search NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;
    IF @Take > 20 SET @Take = 20;

    -- A search CANNOT be settled here, and this procedure deliberately does not
    -- pretend otherwise. Half the text the card shows -- a blocked PROVIDER's
    -- business name -- lives in their Cosmos offering document, so a SQL LIKE
    -- would silently miss exactly the name a parent is most likely to type
    -- ("Happy Paws Hotel", not the owner's own name). Filtering here and paging
    -- here would then hand back a page that is wrong in both directions: missing
    -- rows that match, and counting rows that do not.
    --
    -- So when a term is supplied this procedure hands back the caller's WHOLE
    -- blocked list, in order, and gets out of the way: the application resolves
    -- the business names, filters on name OR business name, and pages what is
    -- left. The set is one person's own blocks, so it is small by construction.
    -- [TotalCount] is still the true unfiltered total; the caller recomputes the
    -- filtered one and ignores this.
    IF @Search IS NOT NULL AND LTRIM(RTRIM(@Search)) <> N''
    BEGIN
        SET @Skip = 0;
        SET @Take = 2147483647;
    END

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
           COUNT(*) OVER() AS [TotalCount],
           -- Appended LAST on purpose, so every ordinal above it -- [TotalCount]
           -- included -- keeps the position its reader already takes it from.
           --
           -- The clear watermark is the caller's own participant row: a thread
           -- they have cleared shows nothing until the counterparty writes again,
           -- exactly as it behaves in their inbox. `me` is LEFT JOINed rather
           -- than INNER, so a conversation whose participant row is somehow
           -- missing still shows its preview rather than the pair silently
           -- looking as though they had never spoken.
           CASE WHEN conv.[ConversationId] IS NOT NULL
                     AND (me.[DeletedAtUtc] IS NULL
                          OR conv.[LastSequence] > COALESCE(me.[ClearedUpToSequence], 0))
                THEN conv.[LastMessagePreview]
           END AS [LastMessagePreview]
    FROM [Block].[BlockedParticipants] b
    LEFT JOIN [Provider].[Providers] pr
        ON b.[BlockedType] = N'Provider' AND pr.[ProviderId] = b.[BlockedId]
    LEFT JOIN [Parent].[PetParents] pp
        ON b.[BlockedType] = N'PetParent' AND pp.[PetParentId] = b.[BlockedId]
    LEFT JOIN [Provider].[ProviderServiceRegistrations] reg
        ON b.[BlockedType] = N'Provider' AND reg.[ProviderId] = b.[BlockedId]
    -- A block always runs provider <-> parent, so the pair is whichever way round
    -- the two sides fall -- and [UQ_Conversations_Pair] makes this at most one
    -- row, which is what lets it join without paging risk.
    LEFT JOIN [Chat].[Conversations] conv
        ON conv.[ProviderId] = CASE WHEN b.[BlockerType] = N'Provider'
                                    THEN b.[BlockerId] ELSE b.[BlockedId] END
       AND conv.[PetParentId] = CASE WHEN b.[BlockerType] = N'PetParent'
                                     THEN b.[BlockerId] ELSE b.[BlockedId] END
    LEFT JOIN [Chat].[ConversationParticipants] me
        ON me.[ConversationId] = conv.[ConversationId]
       AND me.[ParticipantType] = @BlockerType
       AND me.[ParticipantId] = @BlockerId
    WHERE b.[BlockerType] = @BlockerType
      AND b.[BlockerId] = @BlockerId
    ORDER BY b.[CreatedAtUtc] DESC, b.[BlockId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

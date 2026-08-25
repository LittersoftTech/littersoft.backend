-- The caller's chat inbox: their threads, most recently active first, with the
-- counterparty's name and their own unread count on each card. Optionally
-- filtered by @Search — the inbox search bar.
--
-- One indexed read, not a fan-out. That is what the denormalised last-message
-- columns on [Chat].[Conversations] are for — without them this screen would need
-- a Cosmos query per thread just to show a preview line. Search is applied to
-- that SAME read rather than to a second store, which is why it costs nothing
-- extra and pages identically.
--
-- Threads that exist but have never been used sort last (LastMessageAtUtc NULL),
-- rather than being hidden: a conversation is created the moment someone opens
-- it, and a parent who opened a provider's thread and hesitated should still find
-- it where they left it.
--
-- Names are joined LIVE. A review, a booking and a chat all read a
-- counterparty's name this way for the same reason: an anonymised account must
-- read "Deleted Provider" / "Deleted User" everywhere, and a denormalised copy
-- would keep the real name. Searching therefore matches whatever the caller can
-- actually SEE — including "Deleted User".
--
-- Returns ONE result set. Paged by the caller; @Take is capped there.
CREATE OR ALTER PROCEDURE [Chat].[ListConversations]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20,
    -- Free text from the inbox search bar. NULL or blank returns the unfiltered
    -- inbox, so the same procedure serves both and there is no second code path
    -- for the two to drift apart in.
    @Search NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;

    -- A "contains" match, with the LIKE metacharacters in the user's own text
    -- neutralised — otherwise typing '%' would match every thread and '_' would
    -- match any character, which is a surprising search box at best. '[' is
    -- escaped FIRST: the other two replacements introduce brackets of their own,
    -- so doing it later would re-escape them. Same treatment the event-title
    -- search gives its term.
    --
    -- Case-insensitivity comes from the database collation, as it does for the
    -- event search — no LOWER() on the column, which would make the predicate
    -- non-sargable for no benefit here.
    DECLARE @Pattern NVARCHAR(220) = NULL;

    IF @Search IS NOT NULL AND LTRIM(RTRIM(@Search)) <> N''
    BEGIN
        SET @Pattern = N'%' + REPLACE(REPLACE(REPLACE(
            LTRIM(RTRIM(@Search)), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
    END

    -- The counterparty name is a CASE expression over a LEFT JOIN, and a computed
    -- alias cannot be referenced from WHERE — hence the derived table, which lets
    -- the search predicate read the name exactly as the card will show it rather
    -- than repeating the expression.
    SELECT t.[ConversationId],
           t.[ProviderId],
           t.[PetParentId],
           t.[LastSequence],
           t.[LastMessageAtUtc],
           t.[LastMessagePreview],
           t.[LastMessageSenderType],
           t.[CreatedAtUtc],
           t.[LastReadSequence],
           t.[UnreadCount],
           t.[IsMuted],
           t.[CounterpartyType],
           t.[CounterpartyId],
           t.[CounterpartyName],
           t.[CounterpartyPhotoUrl],
           t.[CounterpartyServiceCategory],
           t.[IsBlocked],
           t.[BlockedByMe]
    FROM (
        SELECT c.[ConversationId],
               c.[ProviderId],
               c.[PetParentId],
               c.[LastSequence],
               c.[LastMessageAtUtc],
               c.[LastMessagePreview],
               c.[LastMessageSenderType],
               c.[CreatedAtUtc],
               p.[LastReadSequence],
               p.[UnreadCount],
               CAST(p.[IsMuted] AS BIT) AS [IsMuted],
               CASE WHEN @ParticipantType = N'Provider' THEN N'PetParent' ELSE N'Provider' END
                   AS [CounterpartyType],
               CASE WHEN @ParticipantType = N'Provider' THEN c.[PetParentId] ELSE c.[ProviderId] END
                   AS [CounterpartyId],
               CASE WHEN @ParticipantType = N'Provider'
                    THEN LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
                    ELSE LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
               END AS [CounterpartyName],
               -- Parent photos only; a provider's image is in Cosmos. The caller
               -- batch-resolves those for the page it is returning.
               CASE WHEN @ParticipantType = N'Provider' THEN pp.[ProfilePhotoUrl] END
                   AS [CounterpartyPhotoUrl],
               -- ...and this is what lets it. A provider's offering document is
               -- partitioned by ServiceCategory, so a point read needs the category as
               -- well as the id, and a chat thread — unlike a booking — carries no
               -- service to get it from. Handing it over here keeps the resolution to
               -- one Cosmos point read per provider instead of a SQL round trip first.
               -- NULL when the counterparty is a pet parent (their photo is already
               -- above), and when the provider has registered no service yet, which is
               -- legitimate: chat is open to a provider mid-onboarding.
               CASE WHEN @ParticipantType = N'PetParent' THEN reg.[ServiceCategory] END
                   AS [CounterpartyServiceCategory],
               -- A block closes the thread. The flag is what lets the app disable
               -- the composer up front instead of letting a send fail, and it is
               -- TRUE in EITHER direction, because either direction closes it.
               CAST(CASE WHEN EXISTS (
                        SELECT 1
                        FROM [Block].[BlockedParticipants] bp
                        WHERE (bp.[BlockerType] = N'Provider' AND bp.[BlockerId] = c.[ProviderId]
                               AND bp.[BlockedType] = N'PetParent' AND bp.[BlockedId] = c.[PetParentId])
                           OR (bp.[BlockerType] = N'PetParent' AND bp.[BlockerId] = c.[PetParentId]
                               AND bp.[BlockedType] = N'Provider' AND bp.[BlockedId] = c.[ProviderId])
                    ) THEN 1 ELSE 0 END AS BIT) AS [IsBlocked],
               -- Only the caller's OWN block offers an Unblock button. One placed
               -- against them is never named -- naming it would confirm the other
               -- party acted, which is the thing a block must not do. The pair
               -- reads as: true/true offer Unblock, true/false show a neutral
               -- "not available".
               CAST(CASE WHEN EXISTS (
                        SELECT 1
                        FROM [Block].[BlockedParticipants] bp
                        WHERE bp.[BlockerType] = @ParticipantType
                          AND bp.[BlockerId] = @ParticipantId
                          AND bp.[BlockedId] = CASE WHEN @ParticipantType = N'Provider'
                                                    THEN c.[PetParentId] ELSE c.[ProviderId] END
                    ) THEN 1 ELSE 0 END AS BIT) AS [BlockedByMe]
        FROM [Chat].[ConversationParticipants] p
        INNER JOIN [Chat].[Conversations] c
            ON c.[ConversationId] = p.[ConversationId]
        LEFT JOIN [Provider].[Providers] pr ON pr.[ProviderId] = c.[ProviderId]
        LEFT JOIN [Parent].[PetParents] pp ON pp.[PetParentId] = c.[PetParentId]
        -- UNIQUE on ProviderId, so this cannot fan the page out.
        LEFT JOIN [Provider].[ProviderServiceRegistrations] reg ON reg.[ProviderId] = c.[ProviderId]
        WHERE p.[ParticipantType] = @ParticipantType
          AND p.[ParticipantId] = @ParticipantId
          -- Threads THIS side has cleared ("delete chat"), while nothing has been
          -- said since. The counterparty's copy is unaffected — their row has its
          -- own watermark — and the moment they write again LastSequence passes
          -- the watermark and the thread reappears here, which is what stops a
          -- delete from quietly cutting the caller off. See the column comments
          -- on [Chat].[ConversationParticipants] for why both halves are needed.
          AND (p.[DeletedAtUtc] IS NULL OR c.[LastSequence] > p.[ClearedUpToSequence])
    ) AS t
    WHERE @Pattern IS NULL
       OR t.[CounterpartyName] LIKE @Pattern
       -- The preview is the thread's newest message, already denormalised onto
       -- the conversation for the card — so matching it costs nothing and covers
       -- "whatever we were just talking about". It is NOT full-history search:
       -- bodies live in Cosmos partitioned by conversation, so searching them all
       -- would be a cross-partition scan per keystroke.
       OR t.[LastMessagePreview] LIKE @Pattern
    -- Newest activity first; never-used threads fall to the bottom. The
    -- ConversationId tie-break is what stops OFFSET paging repeating or skipping
    -- a row when two threads share a timestamp — the same reason the review and
    -- earnings lists carry one.
    ORDER BY t.[LastMessageAtUtc] DESC, t.[ConversationId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

-- One party's tickets — the "my tickets" list on either app.
--
-- Scoped by the CALLER, not by direction: a ticket the counterparty raised
-- against you is as much yours as one you raised, and both need answering. The
-- row carries [RaisedByType] so the card can say which way round it is.
--
-- BOTH ticket types come back in one list, discriminated by [TicketType]. A user
-- thinks "my tickets", not "my two kinds of tickets" — the same reasoning that
-- merges the two booking kinds in the parent's history feed.
--
-- @Statuses is a plain CSV, expanded in C#. Keeping the vocabulary there rather
-- than teaching this procedure about groups like "open" means adding a status is
-- an edit to one C# file instead of to every reader.
--
-- Returns ONE result set — the page, with the whole-population [TotalCount]
-- appended as a window so one round trip serves both the rows and the total.
CREATE OR ALTER PROCEDURE [Support].[ListTickets]
    @ActorType NVARCHAR(16),                    -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER,
    @Statuses NVARCHAR(MAX) = NULL,
    @SortBy NVARCHAR(16) = N'UpdatedAt',        -- 'CreatedAt' | 'UpdatedAt'
    @SortDirection NVARCHAR(8) = N'Desc',       -- 'Asc' | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take <= 0 SET @Take = 20;
    IF @Take > 20 SET @Take = 20;

    SELECT t.[TicketId],
           t.[TicketNumber],
           t.[TicketType],
           t.[ProviderId],
           t.[PetParentId],
           t.[RaisedByType],
           t.[BookingType],
           t.[BookingId],
           t.[PetId],
           t.[ConversationId],
           t.[Status],
           t.[CreatedAtUtc],
           t.[UpdatedAtUtc],
           t.[ClosedAtUtc],
           -- Appended after [ClosedAtUtc] to keep the ticket block contiguous and
           -- identical to the other four procedures — which pushes [TotalCount]
           -- from ordinal 14 to 16. Its reader in SqlSupportTicketStore reads it
           -- positionally, so the two must move together.
           t.[Category],
           t.[Reason],
           COUNT(*) OVER () AS [TotalCount]
    FROM [Support].[Tickets] AS t
    WHERE ((@ActorType = N'Provider'  AND t.[ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND t.[PetParentId] = @ActorId))
      AND (@Statuses IS NULL OR @Statuses = N''
           OR t.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')))
    ORDER BY
        CASE WHEN @SortBy = N'CreatedAt' AND @SortDirection = N'Asc'  THEN t.[CreatedAtUtc] END ASC,
        CASE WHEN @SortBy = N'CreatedAt' AND @SortDirection = N'Desc' THEN t.[CreatedAtUtc] END DESC,
        CASE WHEN @SortBy <> N'CreatedAt' AND @SortDirection = N'Asc'  THEN t.[UpdatedAtUtc] END ASC,
        CASE WHEN @SortBy <> N'CreatedAt' AND @SortDirection = N'Desc' THEN t.[UpdatedAtUtc] END DESC,
        -- Deterministic tie-break. Without it OFFSET paging can repeat or skip
        -- rows sharing a timestamp, which tickets raised in a burst will.
        t.[TicketId] ASC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;

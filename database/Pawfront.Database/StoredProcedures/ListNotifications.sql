-- The in-app notification inbox for one recipient, newest-first.
--
-- Only RENDERED rows are returned ([Title] IS NOT NULL). A row sits unrendered
-- for at most one dispatcher tick, and showing a blank entry in the bell menu
-- would be worse than showing it a minute late.
--
-- Result set 1: the page of notifications.
-- Result set 2: { TotalCount, UnreadCount } across the whole inbox (not the
--               page), so the client can render the unread badge and paginate
--               without a second call.
CREATE OR ALTER PROCEDURE [Notification].[ListNotifications]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 50,
    @UnreadOnly BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 50;
    IF @Take > 200 SET @Take = 200;

    SELECT [NotificationId],
           [NotificationType],
           [Title],
           [Body],
           [Route],
           [EntityType],
           [EntityId],
           [DataJson],
           [ImageUrl],
           [CreatedAtUtc],
           [ReadAtUtc]
    FROM [Notification].[NotificationOutbox]
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [Title] IS NOT NULL
      AND (@UnreadOnly = 0 OR [ReadAtUtc] IS NULL)
    ORDER BY [CreatedAtUtc] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

    -- COALESCE because SUM over zero rows is NULL, not 0 — an inbox with nothing
    -- in it yet would otherwise report a null unread badge.
    SELECT COUNT(*) AS [TotalCount],
           COALESCE(SUM(CASE WHEN [ReadAtUtc] IS NULL THEN 1 ELSE 0 END), 0) AS [UnreadCount]
    FROM [Notification].[NotificationOutbox]
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [Title] IS NOT NULL;
END

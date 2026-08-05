-- Marks a recipient's notifications as read. Pass @NotificationId to mark one,
-- or leave it NULL to mark every unread notification in that inbox ("mark all
-- as read").
--
-- Always scoped by (@Audience, @RecipientId), so a caller can never mark someone
-- else's notification read even with a valid NotificationId — no THROW for a
-- foreign or unknown id, it simply matches nothing and reports 0.
CREATE OR ALTER PROCEDURE [Notification].[MarkNotificationsRead]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE [Notification].[NotificationOutbox]
    SET [ReadAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [ReadAtUtc] IS NULL
      AND [Title] IS NOT NULL
      AND (@NotificationId IS NULL OR [NotificationId] = @NotificationId);

    DECLARE @Updated INT = @@ROWCOUNT;

    SELECT @Updated AS [MarkedCount],
           (SELECT COUNT(*)
            FROM [Notification].[NotificationOutbox]
            WHERE [Audience] = @Audience
              AND [RecipientId] = @RecipientId
              AND [Title] IS NOT NULL
              AND [ReadAtUtc] IS NULL) AS [UnreadCount];
END

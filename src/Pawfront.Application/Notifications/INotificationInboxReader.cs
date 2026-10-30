namespace Pawfront.Application.Notifications;

public sealed record NotificationInboxItem(
    string NotificationId, string Title, string Body, string Type,
    IReadOnlyDictionary<string, string> Data, DateTimeOffset CreatedAt, bool IsRead);

public sealed record NotificationInboxPage(
    IReadOnlyList<NotificationInboxItem> Items, string? NextCursor);

public interface INotificationInboxReader
{
    Task<NotificationInboxPage> ListAsync(NotificationAudience audience, string firebaseUserId,
        int take, string? cursor, CancellationToken cancellationToken);
}

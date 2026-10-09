namespace Pawfront.Application.Notifications;

public interface INotificationReadStateStore
{
    Task<bool> MarkReadAsync(NotificationAudience audience, string firebaseUserId,
        string notificationId, CancellationToken cancellationToken);
    Task MarkAllReadAsync(NotificationAudience audience, string firebaseUserId,
        CancellationToken cancellationToken);
}

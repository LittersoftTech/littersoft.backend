using Microsoft.Extensions.Logging;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

/// <summary>
/// Mirrors each push into Firestore independently of FCM success. Both callers
/// are background workers: observe the write before they finish, without adding
/// a Firestore wait to an API request or leaving an unobserved task at shutdown.
/// </summary>
public sealed class InboxPushSender(
    IPushSender pushSender,
    INotificationInboxWriter inboxWriter,
    ILogger<InboxPushSender> logger) : IPushSender
{
    public async Task<PushSendResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var inboxTask = WriteInboxAsync(message, cancellationToken);
        try
        {
            return await pushSender.SendAsync(message, cancellationToken);
        }
        finally
        {
            await inboxTask;
        }
    }

    private async Task WriteInboxAsync(PushMessage message, CancellationToken cancellationToken)
    {
        // Bound the best-effort write so an unavailable Firestore cannot hold
        // the worker's outbox lease for the SDK's full retry window.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await inboxWriter.WriteAsync(message, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Firestore inbox write failed for notification {NotificationId} ({Audience}); FCM outcome is unchanged.",
                message.Data.GetValueOrDefault("notificationId"), message.Audience);
        }
    }
}

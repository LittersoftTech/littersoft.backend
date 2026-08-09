using Microsoft.Extensions.Logging;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.Notifications;

/// <summary>
/// In-memory-mode fallback for <see cref="IInstantNotificationSender"/>.
///
/// There is no outbox to close out in that configuration — nothing enqueued a row
/// in the first place, since the in-memory chat store cannot — so this only has
/// to exist for the chat host's push worker to resolve. Same posture as
/// <see cref="NullNotificationPublisher"/>.
/// </summary>
internal sealed class NullInstantNotificationSender(ILogger<NullInstantNotificationSender> logger)
    : IInstantNotificationSender
{
    public Task CompleteAsync(NotificationDeliveryResult result, CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "No SQL outbox is configured; dropping the delivery outcome for notification {NotificationId}.",
            result.NotificationId);

        return Task.CompletedTask;
    }
}

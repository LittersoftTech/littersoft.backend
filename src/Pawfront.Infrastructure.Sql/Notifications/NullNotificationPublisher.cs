using Microsoft.Extensions.Logging;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.Notifications;

/// <summary>
/// Dev fallback used when no SQL connection string is configured (the in-memory
/// store path). Logs what would have been sent and drops it.
///
/// A developer on the in-memory store gets no notifications at all — the same
/// posture as the booking sweeps, which likewise have no in-memory equivalent.
/// </summary>
internal sealed class NullNotificationPublisher(ILogger<NullNotificationPublisher> logger)
    : INotificationPublisher
{
    public Task<Guid> PublishAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[NoOp] Notification {NotificationType} for {Audience} {RecipientId} was not enqueued " +
            "(no SQL store configured).",
            request.NotificationType, request.Audience, request.RecipientId);

        return Task.FromResult(Guid.Empty);
    }

    public Task PublishManyAsync(
        IReadOnlyList<NotificationRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            logger.LogInformation(
                "[NoOp] Notification {NotificationType} for {Audience} {RecipientId} was not enqueued " +
                "(no SQL store configured).",
                request.NotificationType, request.Audience, request.RecipientId);
        }

        return Task.CompletedTask;
    }
}

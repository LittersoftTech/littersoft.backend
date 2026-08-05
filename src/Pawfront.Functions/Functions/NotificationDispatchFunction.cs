using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Pawfront.Functions.Notifications;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Drains the notification outbox to FCM every minute.
///
/// It lives in the same Function App as <see cref="BookingSweepFunction"/> and
/// relies on the same guarantee: the Functions host serialises a timer trigger's
/// invocations across however many instances the app scales to, so exactly one
/// dispatch runs per tick and a notification is never sent twice. That holds only
/// while this stays the ONE deployed Function App for these triggers.
///
/// One minute is the floor on delivery latency, and the reason the outbox is
/// worth it anyway: notifications originate in three separate processes (both API
/// hosts and the booking sweep), and only a durable queue can carry all three
/// with retries.
/// </summary>
public sealed class NotificationDispatchFunction(
    NotificationDispatcher dispatcher,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<NotificationDispatchFunction>();

    [Function("NotificationDispatchFunction")]
    public async Task Run(
        [TimerTrigger("0 */1 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await dispatcher.RunAsync(cancellationToken);

            if (!result.DidWork)
            {
                return;
            }

            _logger.LogInformation(
                "Notification dispatch: claimed {Claimed}, sent {Sent}, no device {NoDevice}, " +
                "failed {Failed}, deactivated {DeactivatedTokens} dead token(s).",
                result.Claimed, result.Sent, result.NoDevice, result.Failed, result.DeactivatedTokens);

            if (result.Failed > 0)
            {
                _logger.LogWarning(
                    "{Failed} notification(s) failed to dispatch and will be retried with backoff.",
                    result.Failed);
            }
        }
        catch (Exception exception)
        {
            // Claimed rows hold a lease rather than being lost: they become
            // eligible again once it lapses, so a transient outage self-heals
            // without this tick failing the host.
            _logger.LogError(exception, "Notification dispatch failed; will retry at the next scheduled tick.");
        }
    }
}

namespace Pawfront.Application.Notifications;

/// <summary>
/// Reports the outcome of a notification the CALLER sent itself, rather than
/// leaving to the 1-minute dispatcher.
///
/// This is the second half of the instant-push design.
/// <c>Notification.EnqueueInstantNotification</c> writes the outbox row ALREADY
/// CLAIMED — status <c>Sending</c>, lease pushed forward — so the timer skips it
/// while the caller works. This closes the row out through the very same
/// <c>Notification.CompleteNotificationDelivery</c> procedure the dispatcher
/// uses, which is what keeps the retry maths, the backoff and the in-app inbox
/// write in one place.
///
/// If this is never called — the process died mid-send — the lease simply lapses
/// and the ordinary dispatcher picks the row up on its next tick. That is the
/// whole safety net: the outbox stops being the delivery path and becomes the
/// backstop, with no second code path to keep in step.
/// </summary>
public interface IInstantNotificationSender
{
    /// <summary>
    /// Records what happened to one notification. Must not throw for ordinary
    /// failures: by the time this runs the message is already stored and very
    /// possibly delivered, so failing here would be reporting a problem about a
    /// problem.
    /// </summary>
    Task CompleteAsync(NotificationDeliveryResult result, CancellationToken cancellationToken);
}

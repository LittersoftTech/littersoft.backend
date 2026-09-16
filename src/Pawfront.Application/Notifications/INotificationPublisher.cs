namespace Pawfront.Application.Notifications;

/// <summary>
/// One notification to enqueue. Carries no copy — the dispatcher renders
/// <see cref="NotificationType"/> + <see cref="Data"/> through
/// <see cref="NotificationTemplateCatalog"/>, so wording stays in one place.
/// </summary>
/// <param name="Audience">Which app (and therefore which Firebase project) to send to.</param>
/// <param name="RecipientId">ProviderId or PetParentId, per <paramref name="Audience"/>.</param>
/// <param name="NotificationType">A key from <see cref="NotificationTypes"/>.</param>
/// <param name="EntityType">A value from <see cref="NotificationEntityTypes"/>.</param>
/// <param name="EntityId">The booking / event / ticket the notification is about.</param>
/// <param name="Data">
/// Flat template parameters, also forwarded to the app in the FCM <c>data</c>
/// payload. Values must be strings — FCM rejects anything else.
/// </param>
/// <param name="ImageUrl">
/// Optional rich image. Must be publicly fetchable: the OS downloads it
/// anonymously, so a bare private-container blob URL will not render.
/// </param>
/// <param name="DedupeKey">
/// Idempotency key (e.g. <c>BOOKING_ACCEPTED:{bookingId}</c>). A repeat enqueue
/// returns the existing notification rather than creating a second inbox entry.
/// Null when repeats are legitimately distinct events.
/// </param>
public sealed record NotificationRequest(
    NotificationAudience Audience,
    Guid RecipientId,
    string NotificationType,
    string? EntityType = null,
    Guid? EntityId = null,
    IReadOnlyDictionary<string, string>? Data = null,
    string? ImageUrl = null,
    string? DedupeKey = null);

/// <summary>
/// Writes notifications to the transactional outbox. This is the ONLY way the
/// API hosts produce a notification — they never talk to FCM directly, which is
/// what keeps an external HTTP call off the request path and lets the
/// Pawfront.Functions sweep (which has no request context at all) produce
/// identical notifications from T-SQL.
///
/// Implementations must not throw for ordinary failures: a notification is never
/// a good reason to fail the booking transaction that caused it.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Enqueues one notification and returns its id, or <see cref="Guid.Empty"/>
    /// if it could not be enqueued (already logged — the caller should carry on).
    /// </summary>
    Task<Guid> PublishAsync(NotificationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues several notifications — typically the two sides of one event
    /// (the parent's "accepted" and the provider's copy). Failures are swallowed
    /// per item for the reason above.
    /// </summary>
    Task PublishManyAsync(
        IReadOnlyList<NotificationRequest> requests,
        CancellationToken cancellationToken);
}

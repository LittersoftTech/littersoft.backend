namespace Pawfront.Application.Notifications;

/// <summary>A notification claimed for dispatch. Copy is not yet rendered.</summary>
public sealed record ClaimedNotification(
    Guid NotificationId,
    NotificationAudience Audience,
    Guid RecipientId,
    string NotificationType,
    string? EntityType,
    Guid? EntityId,
    string? DataJson,
    string? ImageUrl,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc);

/// <summary>One active device to push to.</summary>
public sealed record RecipientDeviceToken(
    NotificationAudience Audience,
    Guid RecipientId,
    string FcmToken,
    string? DevicePlatform);

/// <summary>
/// A claimed batch plus the device tokens for its recipients, fetched together so
/// the dispatcher never issues a per-notification token lookup.
/// </summary>
public sealed record NotificationClaimBatch(
    IReadOnlyList<ClaimedNotification> Notifications,
    IReadOnlyList<RecipientDeviceToken> DeviceTokens)
{
    public static readonly NotificationClaimBatch Empty = new([], []);

    public bool IsEmpty => Notifications.Count == 0;
}

/// <summary>Terminal states a dispatch attempt can report.</summary>
public static class NotificationDeliveryStatuses
{
    public const string Sent = "Sent";

    /// <summary>
    /// A success, not a failure: the notification is real and belongs in the
    /// in-app inbox, the recipient simply has no active device token.
    /// </summary>
    public const string NoDevice = "NoDevice";

    /// <summary>Retried with backoff until the attempt ceiling, then terminal.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// The outcome of one dispatch, carrying the copy the dispatcher rendered so it
/// can be persisted for the in-app inbox in the same round-trip.
/// </summary>
public sealed record NotificationDeliveryResult(
    Guid NotificationId,
    string Status,
    string? Title,
    string? Body,
    string? Route,
    int DeliveredCount,
    string? LastError);

/// <summary>A token Firebase reported as permanently invalid.</summary>
public sealed record DeadDeviceToken(NotificationAudience Audience, string FcmToken);

/// <summary>
/// The dispatcher's view of the outbox: claim work, report outcomes, prune dead
/// tokens. Separate from <see cref="INotificationPublisher"/> because the API
/// hosts only ever write, and only the Pawfront.Functions dispatcher reads.
/// </summary>
public interface INotificationOutboxStore
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due notifications and returns them
    /// with their recipients' active device tokens. Claiming takes a lease, so a
    /// dispatcher that dies mid-batch releases its rows automatically.
    /// </summary>
    Task<NotificationClaimBatch> ClaimAsync(
        int batchSize,
        int maxAttempts,
        int leaseMinutes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records outcomes and persists rendered copy. Successes become terminal;
    /// failures are rescheduled with backoff until the attempt ceiling.
    /// </summary>
    Task CompleteAsync(
        IReadOnlyList<NotificationDeliveryResult> results,
        int maxAttempts,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates tokens Firebase rejected as permanently invalid, so the next
    /// tick stops pushing to devices that no longer exist.
    /// </summary>
    Task DeactivateTokensAsync(
        IReadOnlyList<DeadDeviceToken> tokens,
        CancellationToken cancellationToken);
}

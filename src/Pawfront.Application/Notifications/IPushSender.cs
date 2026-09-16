namespace Pawfront.Application.Notifications;

/// <summary>
/// One push, fanned out to every active device of a single recipient.
/// </summary>
/// <param name="Audience">Selects the Firebase project to send with.</param>
/// <param name="Tokens">The recipient's active FCM tokens. Never empty.</param>
/// <param name="Data">
/// The complete FCM <c>data</c> payload, already assembled by
/// <see cref="NotificationPayloadBuilder"/>. All values are strings — FCM
/// rejects any other type.
/// </param>
public sealed record PushMessage(
    NotificationAudience Audience,
    IReadOnlyList<string> Tokens,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string? ImageUrl);

/// <summary>
/// The result of one fan-out.
/// </summary>
/// <param name="SuccessCount">Devices that accepted the message.</param>
/// <param name="InvalidTokens">
/// Tokens Firebase reported as PERMANENTLY invalid (UNREGISTERED,
/// INVALID_ARGUMENT, SENDER_ID_MISMATCH) — these get deactivated. Transient
/// failures (UNAVAILABLE, INTERNAL, quota) are deliberately NOT listed here:
/// deactivating a live device because Firebase had a bad minute would silently
/// stop that user's notifications forever.
/// </param>
/// <param name="Error">
/// Set when the whole send failed (auth, network, misconfiguration) rather than
/// individual tokens — the notification is then retried.
/// </param>
public sealed record PushSendResult(
    int SuccessCount,
    IReadOnlyList<string> InvalidTokens,
    string? Error)
{
    public bool IsFailure => Error is not null;

    public static PushSendResult Failed(string error) => new(0, [], error);
}

/// <summary>
/// Sends an assembled push to FCM. The only abstraction over Firebase in the
/// codebase — implemented once, in Pawfront.Infrastructure.Firebase, and resolved
/// only by the Pawfront.Functions dispatcher. Neither API host references it,
/// which is a deliberate consequence of the outbox design.
/// </summary>
public interface IPushSender
{
    Task<PushSendResult> SendAsync(PushMessage message, CancellationToken cancellationToken);
}

using Microsoft.Extensions.Logging;
using Pawfront.Application.Notifications;

namespace Pawfront.Application.Chat;

/// <summary>
/// One queued push: an outbox row already claimed by this process, with
/// everything needed to render and send it.
/// </summary>
/// <param name="Tokens">
/// The recipient's active devices, read in the same round trip that queued the
/// row. Never empty — a recipient with no devices has no notification queued in
/// the first place.
/// </param>
public sealed record ChatPushWorkItem(
    Guid NotificationId,
    NotificationAudience Audience,
    Guid RecipientId,
    Guid ConversationId,
    string? DataJson,
    IReadOnlyList<string> Tokens);

/// <summary>
/// Hands a queued push to whatever will actually send it.
///
/// It exists so <see cref="IChatService"/> can stay synchronous about delivery
/// while the FCM call — the one genuinely slow leg, an outbound HTTPS request —
/// happens off the sender's request. The realtime fan-out is NOT queued: that one
/// is fast and its latency is the entire point of the feature.
///
/// Dropping an item is survivable by construction. The outbox row is already
/// written and leased, so anything not sent here is picked up by the ordinary
/// 1-minute dispatcher once its lease lapses.
/// </summary>
public interface IChatPushDispatcher
{
    /// <summary>
    /// Queues the push. Returns immediately and never throws — the caller has
    /// already stored the message, and a delivery problem must not surface as a
    /// failed send.
    /// </summary>
    void Enqueue(ChatPushWorkItem item);
}

/// <summary>
/// Stand-in for hosts with chat services registered but no sender of their own.
///
/// Dropping here costs nothing: the row is claimed but un-completed, so its lease
/// lapses and <c>NotificationDispatchFunction</c> sends it within a minute or
/// two. The user gets the notification either way — just not instantly.
/// </summary>
public sealed class NullChatPushDispatcher(ILogger<NullChatPushDispatcher> logger) : IChatPushDispatcher
{
    public void Enqueue(ChatPushWorkItem item) =>
        logger.LogDebug(
            "No instant push dispatcher is registered; notification {NotificationId} will be delivered " +
            "by the scheduled dispatcher once its lease lapses.",
            item.NotificationId);
}

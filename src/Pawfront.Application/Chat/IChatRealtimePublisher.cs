using Microsoft.Extensions.Logging;

namespace Pawfront.Application.Chat;

/// <summary>
/// Pushes a chat event out over the live connection.
///
/// It is an abstraction rather than a direct SignalR call so that Application
/// never references SignalR — the same reason <see cref="Notifications.IPushSender"/>
/// keeps Firebase out of here. The hub implements it in the chat host.
///
/// Every method is fire-and-forget from the caller's point of view: a socket
/// fan-out must never fail a message that is already durably stored. Delivery is
/// reconciled by the client on reconnect (it re-reads history from its last known
/// sequence), which is what makes best-effort acceptable.
/// </summary>
public interface IChatRealtimePublisher
{
    /// <summary>
    /// Delivers a message to everyone with the thread open, and nudges the
    /// recipient's other devices so their inbox and badge update even when they
    /// are elsewhere in the app.
    /// </summary>
    Task PublishMessageAsync(
        ChatMessage message,
        ChatParticipant recipient,
        int recipientUnreadCount,
        CancellationToken cancellationToken);

    /// <summary>Tells the counterparty how far the caller has now read.</summary>
    Task PublishReadAsync(
        Guid conversationId,
        ChatParticipant reader,
        long lastReadSequence,
        ChatParticipant counterparty,
        CancellationToken cancellationToken);

    /// <summary>
    /// Typing state. Never persisted anywhere — it is meaningless a second later,
    /// so it exists only as a hub broadcast.
    /// </summary>
    Task PublishTypingAsync(
        Guid conversationId,
        ChatParticipant typist,
        bool isTyping,
        ChatParticipant counterparty,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in used until the SignalR hub is wired, and in any host that has chat
/// services registered but no hub of its own.
///
/// It logs and drops, the same posture <c>NullNotificationPublisher</c> takes for
/// the outbox in the in-memory configuration. Chat still works through it —
/// messages persist, history reads, and the push still goes out — clients just
/// have to poll instead of being told.
/// </summary>
public sealed class NullChatRealtimePublisher(ILogger<NullChatRealtimePublisher> logger)
    : IChatRealtimePublisher
{
    public Task PublishMessageAsync(
        ChatMessage message,
        ChatParticipant recipient,
        int recipientUnreadCount,
        CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "No realtime publisher is registered; message {MessageId} on conversation {ConversationId} " +
            "was stored but not pushed over a socket.",
            message.MessageId, message.ConversationId);

        return Task.CompletedTask;
    }

    public Task PublishReadAsync(
        Guid conversationId,
        ChatParticipant reader,
        long lastReadSequence,
        ChatParticipant counterparty,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishTypingAsync(
        Guid conversationId,
        ChatParticipant typist,
        bool isTyping,
        ChatParticipant counterparty,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

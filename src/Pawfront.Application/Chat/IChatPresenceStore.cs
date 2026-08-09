namespace Pawfront.Application.Chat;

/// <summary>
/// Live connections, and which thread each has open.
///
/// This drives exactly one decision: whether a message earns an FCM push. The
/// read side of it is not here — it lives inside <c>Chat.AppendMessage</c>, which
/// checks presence in the same transaction that writes the message, so presence
/// cannot change between the decision and the write.
/// </summary>
public interface IChatPresenceStore
{
    /// <summary>
    /// Registers a connection, or refreshes its heartbeat. An upsert, so a
    /// heartbeat for a connection the stale sweep already purged re-registers it
    /// rather than failing.
    /// </summary>
    Task SaveConnectionAsync(
        string connectionId,
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a connection on disconnect. Best-effort: OnDisconnectedAsync does
    /// not run if the host crashes, which is why
    /// <see cref="PurgeStaleAsync"/> exists.
    /// </summary>
    Task DeleteConnectionAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Records which thread this connection is viewing, or null when the client
    /// has navigated away. Scoped by participant, so a connection cannot suppress
    /// somebody else's push by claiming their id.
    /// </summary>
    Task SetActiveConversationAsync(
        string connectionId,
        ChatParticipant participant,
        Guid? conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops connections whose heartbeat has gone quiet, and returns how many.
    /// A stale row does not merely waste space — it makes the recipient look
    /// present forever and permanently suppresses their pushes.
    /// </summary>
    Task<int> PurgeStaleAsync(int staleMinutes, CancellationToken cancellationToken);
}

namespace Pawfront.Application.Chat;

/// <summary>
/// The Cosmos side of chat: message bodies, partitioned by conversation.
///
/// Everything here is a single-partition operation, which is the point of that
/// partition key — a thread's history never fans out across partitions however
/// long it gets.
/// </summary>
public interface IChatMessageStore
{
    /// <summary>
    /// Point read by id. Used before appending, so a retried send returns the
    /// message that already exists rather than writing a second one.
    /// </summary>
    Task<ChatMessage?> TryGetAsync(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the document. If one already exists with this id — a concurrent
    /// duplicate that slipped past the pre-check — the EXISTING message is
    /// returned rather than overwriting it, because the first write is the one
    /// whose sequence the conversation actually recorded.
    /// </summary>
    Task<ChatMessage> CreateAsync(ChatMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// A page of history, newest first, ending just before
    /// <paramref name="beforeSequence"/> (null starts at the newest).
    /// </summary>
    /// <param name="afterSequence">
    /// An EXCLUSIVE floor: nothing at or below it is returned. This is the
    /// caller's "delete chat" watermark, and it has to be applied here rather than
    /// by the caller, because filtering a page after the fact would return short
    /// pages and a cursor that walks into cleared history forever.
    ///
    /// 0 — the value for anyone who has never cleared the thread — filters
    /// nothing, since sequences start at 1.
    /// </param>
    Task<ChatMessagePage> ListAsync(
        Guid conversationId,
        long? beforeSequence,
        long afterSequence,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retracts a message: clears its text and attachment, stamps
    /// <c>DeletedAtUtc</c>, and leaves the document in place so the thread keeps
    /// its shape and its sequence numbering. Returns null when the message does
    /// not exist or was not sent by <paramref name="sender"/> — one case, so a
    /// message id cannot be probed.
    /// </summary>
    Task<ChatMessage?> SoftDeleteAsync(
        Guid conversationId,
        Guid messageId,
        ChatParticipant sender,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Chat;

/// <summary>
/// What a send produced: the stored message, and everything the delivery leg
/// needs.
/// </summary>
/// <param name="Delivery">
/// Carries the recipient and — when one was queued — the outbox row the caller
/// now owns sending. Returned rather than acted on here because the push is an
/// out-of-band concern: the message is already durable by the time this is
/// handed back, and the caller acknowledges it before delivering.
/// </param>
public sealed record ChatSendResult(ChatMessage Message, ChatAppendResult Delivery);

/// <summary>
/// The chat use cases, composing the SQL thread index
/// (<see cref="IChatConversationStore"/>), the Cosmos message store
/// (<see cref="IChatMessageStore"/>) and the live fan-out
/// (<see cref="IChatRealtimePublisher"/>).
///
/// Both the REST endpoints and the SignalR hub call through here, so a message
/// sent over a socket and one sent over HTTP take exactly the same path — there
/// is no second implementation to keep in step.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Opens the caller's thread with the given counterparty, creating it on
    /// first contact.
    /// </summary>
    Task<ChatConversationDetail> OpenConversationAsync(
        ChatParticipant actor,
        ChatParticipantType counterpartyType,
        Guid counterpartyId,
        CancellationToken cancellationToken);

    /// <summary>The thread header. Throws <see cref="ConversationNotFoundException"/> when it is not the caller's.</summary>
    Task<ChatConversationDetail> GetConversationAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatConversationCard>> ListConversationsAsync(
        ChatParticipant participant,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a message and fans it out over the socket. Idempotent on
    /// <see cref="SendChatMessageCommand.MessageId"/>: resending the same id
    /// returns the message that already exists.
    /// </summary>
    Task<ChatSendResult> SendMessageAsync(
        SendChatMessageCommand command,
        CancellationToken cancellationToken);

    /// <summary>A page of history, newest first. Authorises the caller first.</summary>
    Task<ChatMessagePage> GetHistoryAsync(
        Guid conversationId,
        ChatParticipant participant,
        long? beforeSequence,
        int take,
        CancellationToken cancellationToken);

    Task<ChatParticipantState> MarkReadAsync(
        Guid conversationId,
        ChatParticipant participant,
        long upToSequence,
        CancellationToken cancellationToken);

    Task<ChatUnreadSummary> GetUnreadSummaryAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>Retracts the caller's own message. Throws when it is not theirs.</summary>
    Task<ChatMessage> DeleteMessageAsync(
        Guid conversationId,
        Guid messageId,
        ChatParticipant sender,
        CancellationToken cancellationToken);

    Task<ChatBlock> BlockAsync(
        ChatParticipant blocker,
        ChatParticipantType blockedType,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>Returns null when the block is unknown or not the caller's.</summary>
    Task<ChatBlock?> UnblockAsync(
        Guid chatBlockId,
        ChatParticipant blocker,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatBlock>> ListBlocksAsync(
        ChatParticipant blocker,
        CancellationToken cancellationToken);
}

/// <summary>Shared limits, so the endpoints, the hub and the stores agree on one set.</summary>
public static class ChatLimits
{
    /// <summary>
    /// Longest single message. Generous — chat is where a provider explains a
    /// care instruction — but bounded so one message cannot be a payload.
    /// </summary>
    public const int MaxTextLength = 4000;

    /// <summary>
    /// Matches <c>Chat.Conversations.LastMessagePreview NVARCHAR(200)</c>. The
    /// preview is truncated to fit rather than the column being widened: it is a
    /// one-line inbox summary, not storage.
    /// </summary>
    public const int PreviewLength = 200;

    /// <summary>
    /// Biggest history page. Larger than the 20 the earnings, spend and review
    /// lists cap at, because a chat page legitimately is: scrolling a thread a
    /// screen at a time in 20s would be a lot of round trips.
    /// </summary>
    public const int MaxPageSize = 50;

    public const int DefaultPageSize = 30;

    /// <summary>Inbox page size, matching the other list endpoints in the product.</summary>
    public const int MaxConversationPageSize = 20;

    /// <summary>Shown in the inbox when the newest message is an image with no caption.</summary>
    public const string ImagePreviewLabel = "Photo";
}

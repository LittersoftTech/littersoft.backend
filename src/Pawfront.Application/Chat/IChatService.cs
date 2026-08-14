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
/// The jobs behind a thread — what the chat screen's "View Jobs" shows.
/// </summary>
/// <param name="Jobs">
/// Every booking of either kind between these two, newest first. Carries the same
/// job-card shape the two delete refusals return, enriched with the provider photo
/// and price block SQL cannot supply.
/// </param>
public sealed record ChatConversationJobs(
    Guid ConversationId,
    Guid ProviderId,
    Guid PetParentId,
    IReadOnlyList<ParentOnboarding.PendingParentJob> Jobs,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasMore => Skip + Jobs.Count < TotalCount;
}

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

    /// <summary>
    /// The caller's inbox, optionally filtered by <paramref name="search"/>
    /// (counterparty name and last-message preview — see
    /// <see cref="IChatConversationStore.ListAsync"/> for what that deliberately
    /// does not cover).
    /// </summary>
    Task<IReadOnlyList<ChatConversationCard>> ListConversationsAsync(
        ChatParticipant participant,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// "Delete this chat", for the caller only — the counterparty keeps
    /// everything. Throws <see cref="ConversationNotFoundException"/> when the
    /// thread is unknown or not theirs.
    /// </summary>
    Task<ChatParticipantState> DeleteConversationAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>
    /// The jobs these two have together — the chat screen's "View Jobs". Reads
    /// the pair off the thread after authorising the caller against it, so no
    /// provider or parent id is ever taken from the client.
    /// </summary>
    Task<ChatConversationJobs> GetConversationJobsAsync(
        Guid conversationId,
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

    /// <summary>
    /// Page size for the thread's "View Jobs" list. 20, matching the earnings,
    /// spend and review lists — a jobs list is an ordinary paged list, not a chat
    /// scrollback.
    /// </summary>
    public const int MaxJobsPageSize = 20;

    /// <summary>
    /// Longest inbox search term accepted. Matches the <c>@Search NVARCHAR(200)</c>
    /// parameter, so a longer one is truncated here rather than silently cut off
    /// by SQL. Nobody searches an inbox with a paragraph.
    /// </summary>
    public const int MaxSearchLength = 200;

    /// <summary>Shown in the inbox when the newest message is an image with no caption.</summary>
    public const string ImagePreviewLabel = "Photo";

    /// <summary>
    /// Replaces the inbox preview when the newest message is retracted.
    ///
    /// A placeholder rather than a blank, and rather than falling back to the
    /// message before it: the soft delete keeps the message in place with its
    /// sequence and timestamp, so it IS still the thread's last activity — only
    /// its content is gone. Reaching back to an older message would put the
    /// preview and the card's own <c>lastMessageAtUtc</c> at odds, and a blank
    /// reads as a bug. Same posture as <see cref="ImagePreviewLabel"/>: the
    /// preview line has always been server-composed text, not a raw echo.
    /// </summary>
    public const string DeletedPreviewLabel = "This message was deleted";
}

namespace Pawfront.Application.Chat;

/// <summary>
/// The SQL side of chat: the thread index, per-side read state, and blocks.
///
/// Everything that has to be consistent and countable lives here — ordering,
/// unread counts, the inbox cache. Message bodies do not: they go to Cosmos via
/// <see cref="IChatMessageStore"/>, which is where the volume belongs.
/// </summary>
public interface IChatConversationStore
{
    /// <summary>
    /// Opens the one thread between these two, creating it on first contact.
    /// Race-safe against a concurrent open of the same pair.
    /// </summary>
    /// <exception cref="ChatCounterpartyNotFoundException">Either party is missing.</exception>
    /// <exception cref="ChatProviderAccountDeletedException">The provider deleted their account.</exception>
    /// <exception cref="ChatBlockedException">One has blocked the other.</exception>
    Task<ChatConversationDetail> GetOrCreateAsync(
        Guid providerId,
        Guid petParentId,
        ChatParticipant actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// The authorisation point read. Returns null when the thread does not exist
    /// OR the caller is not a party to it — deliberately indistinguishable, so an
    /// id cannot be probed for existence. Callers answer 404 for both.
    /// </summary>
    Task<ChatConversationDetail?> GetForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>The caller's inbox, most recently active first.</summary>
    Task<IReadOnlyList<ChatConversationCard>> ListAsync(
        ChatParticipant participant,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Assigns the message its sequence, refreshes the inbox cache, moves both
    /// sides' read state, and — when the recipient is not looking at the thread —
    /// queues their push and returns its id with their device tokens.
    ///
    /// The message BODY is written separately, after this returns. See
    /// <c>Chat.AppendMessage</c> for why that ordering is the accepted trade-off.
    /// </summary>
    /// <exception cref="ConversationNotFoundException">No such thread.</exception>
    /// <exception cref="ChatForbiddenException">The sender is not a party to it.</exception>
    /// <exception cref="ChatBlockedException">One party has blocked the other.</exception>
    Task<ChatAppendResult> AppendMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances the caller's read pointer. Never moves backwards, and is clamped
    /// to the thread's own last sequence. Returns null when the thread is not the
    /// caller's.
    /// </summary>
    Task<ChatParticipantState?> MarkReadAsync(
        Guid conversationId,
        ChatParticipant participant,
        long upToSequence,
        CancellationToken cancellationToken);

    /// <summary>The chat badge: unread messages, and how many threads they span.</summary>
    Task<ChatUnreadSummary> GetUnreadSummaryAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>
    /// Blocks the counterparty. Idempotent — blocking somebody already blocked
    /// returns the existing row.
    /// </summary>
    /// <exception cref="ChatInvalidBlockException">Both parties are on the same side.</exception>
    Task<ChatBlock> BlockAsync(
        ChatParticipant blocker,
        ChatParticipantType blockedType,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lifts a block the caller placed. Returns null when the id is unknown or
    /// belongs to somebody else's block — one case, so a block id cannot be probed.
    /// </summary>
    Task<ChatBlock?> UnblockAsync(
        Guid chatBlockId,
        ChatParticipant blocker,
        CancellationToken cancellationToken);

    /// <summary>
    /// Everyone the caller has blocked. Blocks placed AGAINST them are not
    /// returned: telling someone they have been blocked confirms the other party
    /// acted.
    /// </summary>
    Task<IReadOnlyList<ChatBlock>> ListBlocksAsync(
        ChatParticipant blocker,
        CancellationToken cancellationToken);
}

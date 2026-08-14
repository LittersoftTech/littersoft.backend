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

    /// <summary>
    /// The caller's inbox, most recently active first, optionally filtered by
    /// <paramref name="search"/>.
    /// </summary>
    /// <param name="search">
    /// Free text from the inbox search bar, matched against the counterparty's
    /// name and the thread's last-message preview. Null or blank returns the
    /// unfiltered inbox.
    ///
    /// Both are columns the inbox read already has, so searching is the same one
    /// indexed query rather than a second store. It is deliberately NOT full
    /// message-history search: bodies live in Cosmos partitioned by conversation,
    /// so matching them all would be a cross-partition scan per keystroke.
    /// </param>
    Task<IReadOnlyList<ChatConversationCard>> ListAsync(
        ChatParticipant participant,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// "Delete this chat", for the caller ONLY. Records their clear watermark at
    /// the thread's current last sequence, hides it from their inbox, and zeroes
    /// their unread count. The counterparty's copy — messages, unread, read
    /// pointer — is untouched, and so is the conversation itself.
    ///
    /// Reversible in the way that matters: one new message from the counterparty
    /// pushes the thread past the watermark and it reappears, carrying on from
    /// there. Without that a delete would silently stop the caller receiving,
    /// which is a broken conversation rather than a cleared one.
    ///
    /// Returns null when the thread is unknown OR not the caller's — one case, so
    /// an id cannot be probed.
    /// </summary>
    Task<ChatParticipantState?> DeleteForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>
    /// Phase one of a send: authorises the sender and assigns the message its
    /// sequence. Deliberately changes NOTHING a client can observe — the inbox
    /// cache, the unread count and the push all belong to
    /// <see cref="CommitMessageAsync"/>, which runs only once the body is durable.
    /// That is what makes a failed send leave no trace.
    ///
    /// Idempotent on <paramref name="messageId"/>: a duplicate is handed the
    /// original sequence back rather than taking a new one. See
    /// <see cref="ChatMessageReservation.IsReplay"/>.
    /// </summary>
    /// <exception cref="ConversationNotFoundException">No such thread.</exception>
    /// <exception cref="ChatForbiddenException">The sender is not a party to it.</exception>
    /// <exception cref="ChatBlockedException">One party has blocked the other.</exception>
    Task<ChatMessageReservation> ReserveMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Phase two: refreshes the inbox cache, moves both sides' read state, and —
    /// when the recipient is not looking at the thread — queues their push and
    /// returns its id with their device tokens. Call only after the body is
    /// written.
    ///
    /// Idempotent. Committing the same message twice moves no counter and queues
    /// no second push, which is what makes retrying a send that failed after the
    /// body landed both safe and necessary.
    /// </summary>
    /// <exception cref="ConversationNotFoundException">
    /// No such thread, or no reservation for this message.
    /// </exception>
    Task<ChatAppendResult> CommitMessageAsync(
        Guid conversationId,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases an uncommitted reservation after the body failed to write, so a
    /// retry starts clean. Best-effort: leaving the row behind is harmless, since
    /// a retry of the same id would simply reuse its sequence and finish the send.
    /// A committed reservation is never released — it describes a real message.
    /// </summary>
    Task ReleaseMessageReservationAsync(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites the inbox preview after the newest message has been retracted.
    ///
    /// The preview is a denormalised cache on <c>Chat.Conversations</c> — that is
    /// what lets the inbox render without a Cosmos query per thread — so clearing
    /// a message's content in Cosmos does not touch it, and the deleted text goes
    /// on being shown on the card until something else is sent.
    ///
    /// <paramref name="sequence"/> is a GUARD, not just an argument: the write
    /// applies only while that sequence is still the thread's last. A message
    /// arriving between the delete and this call has already moved the preview
    /// on, and must not be pulled back to a retraction notice. Deleting an older
    /// message therefore no-ops here, which is correct — it was never on the card.
    /// </summary>
    Task RefreshDeletedMessagePreviewAsync(
        Guid conversationId,
        long sequence,
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

namespace Pawfront.Application.Chat;

/// <summary>A thread, as the SQL index holds it.</summary>
public sealed record ChatConversation(
    Guid ConversationId,
    Guid ProviderId,
    Guid PetParentId,
    long LastSequence,
    DateTimeOffset? LastMessageAtUtc,
    string? LastMessagePreview,
    string? LastMessageSenderType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>One side's state on a thread.</summary>
public sealed record ChatParticipantState(
    ChatParticipantType ParticipantType,
    Guid ParticipantId,
    long LastReadSequence,
    int UnreadCount,
    bool IsMuted);

/// <summary>
/// The other party, resolved LIVE at read time — never denormalised onto the
/// conversation. That is what makes a deleted account read "Deleted Provider" /
/// "Deleted User" instead of keeping its real name frozen in the thread.
/// </summary>
/// <param name="PhotoUrl">
/// Populated for a pet-parent counterparty from their SQL row. NULL for a
/// provider: <c>Provider.Providers</c> has no photo column — a provider's image
/// lives in their Cosmos offering document — so the caller resolves that one
/// separately, exactly as the booking detail's <c>providerPhotoUrl</c> has to.
/// </param>
public sealed record ChatCounterparty(
    ChatParticipantType ParticipantType,
    Guid ParticipantId,
    string? Name,
    string? PhotoUrl);

/// <summary>A thread plus the caller's own state and the other party — the header.</summary>
public sealed record ChatConversationDetail(
    ChatConversation Conversation,
    ChatParticipantState Me,
    ChatCounterparty Counterparty);

/// <summary>One row of the inbox list.</summary>
public sealed record ChatConversationCard(
    ChatConversation Conversation,
    ChatParticipantState Me,
    ChatCounterparty Counterparty);

/// <summary>An image attached to a message.</summary>
public sealed record ChatAttachment(
    string BlobUrl,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height);

/// <summary>
/// A message, as stored in Cosmos.
/// </summary>
/// <param name="MessageId">
/// Also the Cosmos document id, and supplied by the client. That is what makes a
/// retried send idempotent for free: a create with an existing id returns 409
/// rather than duplicating, and ids are partition-scoped so a collision across
/// conversations is impossible.
/// </param>
/// <param name="Sequence">
/// Assigned by SQL, and what the thread is ordered and paged by — never the
/// timestamp, since two messages can share a millisecond but never a sequence.
/// </param>
/// <param name="DeletedAtUtc">
/// Set when the sender retracts it. The document survives with its text cleared,
/// so the thread keeps its shape and the client can render "This message was
/// deleted" — the same anonymise-rather-than-remove rule the account and pet
/// deletes follow.
/// </param>
public sealed record ChatMessage(
    Guid MessageId,
    Guid ConversationId,
    long Sequence,
    ChatParticipantType SenderType,
    Guid SenderId,
    ChatMessageKind Kind,
    string? Text,
    ChatAttachment? Attachment,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EditedAtUtc,
    DateTimeOffset? DeletedAtUtc)
{
    public bool IsDeleted => DeletedAtUtc is not null;
}

/// <summary>
/// A page of history, newest first.
/// </summary>
/// <param name="NextBeforeSequence">
/// What to pass as <c>beforeSequence</c> to fetch the next (older) page, or null
/// at the start of the thread. Returned rather than left to the client to derive,
/// so paging cannot drift if the ordering rule ever changes.
/// </param>
public sealed record ChatMessagePage(
    IReadOnlyList<ChatMessage> Messages,
    long? NextBeforeSequence,
    bool HasMore);

/// <summary>The chat badge.</summary>
public sealed record ChatUnreadSummary(int UnreadMessageCount, int UnreadConversationCount);

/// <summary>A block the caller placed.</summary>
public sealed record ChatBlock(
    Guid ChatBlockId,
    ChatParticipantType BlockerType,
    Guid BlockerId,
    ChatParticipantType BlockedType,
    Guid BlockedId,
    string? Reason,
    string? BlockedName,
    string? BlockedPhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Phase one of a send: the sequence SQL assigned, and whether this message has
/// been seen before.
/// </summary>
/// <param name="CreatedAtUtc">
/// When the send was accepted. It is stamped on the Cosmos document, which is
/// written between the two phases — so the timestamp has to come from here rather
/// than from the commit that follows.
/// </param>
/// <param name="IsReplay">
/// True when this message id was already reserved. A retry therefore reuses the
/// ORIGINAL sequence instead of taking a new one, which is what stops a duplicate
/// send from becoming a second message. Enforced by a primary key on
/// <c>(ConversationId, MessageId)</c>, so it holds across the hub and REST alike
/// and does not depend on Cosmos being reachable.
/// </param>
/// <param name="IsCommitted">
/// True when the earlier attempt also finished — body written, recipient
/// notified. The caller returns the stored message and delivers nothing further.
/// A reservation that is a replay but NOT committed is a send that died partway;
/// completing it is exactly what the retry is for.
/// </param>
public sealed record ChatMessageReservation(
    Guid ConversationId,
    Guid MessageId,
    long Sequence,
    DateTimeOffset CreatedAtUtc,
    bool IsReplay,
    bool IsCommitted);

/// <summary>
/// The SQL side of appending a message: the sequence it was given, and everything
/// needed to deliver it.
/// </summary>
/// <param name="NotificationId">
/// The outbox row queued for the recipient, or null when none was — either they
/// have the thread open, or they muted it. Non-null means the caller owns sending
/// it and reporting the outcome before the lease lapses.
/// </param>
/// <param name="RecipientTokens">
/// The recipient's active FCM tokens, returned by the same round trip so delivery
/// needs no follow-up query. Empty when no notification was queued.
/// </param>
/// <param name="NotificationDataJson">
/// The template parameters the enqueue wrote, handed back so the caller can
/// render the copy itself. Copy lives only in the C#
/// <c>NotificationTemplateCatalog</c>, and returning this is what saves the chat
/// host from re-reading the outbox row it has just written. Null when no
/// notification was queued.
/// </param>
public sealed record ChatAppendResult(
    Guid ConversationId,
    Guid MessageId,
    long Sequence,
    DateTimeOffset CreatedAtUtc,
    ChatParticipantType RecipientType,
    Guid RecipientId,
    int RecipientUnreadCount,
    bool RecipientIsViewing,
    bool RecipientIsMuted,
    Guid? NotificationId,
    IReadOnlyList<string> RecipientTokens,
    string? NotificationDataJson);

/// <summary>What a caller asks to send.</summary>
public sealed record SendChatMessageCommand(
    Guid ConversationId,
    ChatParticipant Sender,
    Guid MessageId,
    ChatMessageKind Kind,
    string? Text,
    ChatAttachment? Attachment);

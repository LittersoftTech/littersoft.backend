namespace Pawfront.Contracts.Chat;

/// <summary>
/// Opens (or reopens) the caller's thread with somebody.
/// </summary>
/// <param name="CounterpartyId">
/// The other party's id. Their TYPE is not sent: a thread always runs provider
/// &lt;-&gt; pet parent, so the server derives it from whichever app the caller's
/// token came from. One less field to get wrong, and one less thing a client
/// could contradict itself about.
/// </param>
public sealed record OpenConversationRequest(Guid CounterpartyId);

/// <param name="Name">
/// Resolved live at read time, so a deleted account reads "Deleted Provider" /
/// "Deleted User" rather than its old name.
/// </param>
/// <param name="PhotoUrl">
/// Present for a pet-parent counterparty. Null for a provider — their image
/// lives in their Cosmos offering document, not on the SQL row this joins.
/// </param>
public sealed record ChatCounterpartyResponse(
    string ParticipantType,
    Guid ParticipantId,
    string? Name,
    string? PhotoUrl);

/// <param name="LastSequence">
/// The newest message's sequence. Compare with <paramref name="LastReadSequence"/>
/// to know where the "unread" divider goes, and pass it to
/// <c>POST /conversations/{id}/read</c> once the user has seen the thread.
/// </param>
public sealed record ConversationResponse(
    Guid ConversationId,
    Guid ProviderId,
    Guid PetParentId,
    ChatCounterpartyResponse Counterparty,
    long LastSequence,
    DateTimeOffset? LastMessageAtUtc,
    string? LastMessagePreview,
    string? LastMessageSenderType,
    long LastReadSequence,
    int UnreadCount,
    bool IsMuted,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatAttachmentPayload(
    string BlobUrl,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height);

/// <param name="ClientMessageId">
/// A GUID the client mints before sending. It becomes the message id, which is
/// what makes a retry safe: resending after a dropped response returns the
/// original message instead of posting it twice. Omit it and the server mints
/// one — but then a retry WILL duplicate, so clients should always send it.
/// </param>
/// <param name="Kind">
/// <c>Text</c> (default) or <c>Image</c>. An image needs
/// <paramref name="Attachment"/>; upload the file first, then send its url here.
/// </param>
public sealed record SendMessageRequest(
    Guid? ClientMessageId,
    string? Kind,
    string? Text,
    ChatAttachmentPayload? Attachment);

/// <param name="IsDeleted">
/// True once the sender retracts it. The message keeps its place and its
/// sequence — only its content is cleared — so render it as "This message was
/// deleted" rather than removing it.
/// </param>
public sealed record ChatMessageResponse(
    Guid MessageId,
    Guid ConversationId,
    long Sequence,
    string SenderType,
    Guid SenderId,
    string Kind,
    string? Text,
    ChatAttachmentPayload? Attachment,
    bool IsDeleted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EditedAtUtc,
    DateTimeOffset? DeletedAtUtc);

/// <param name="NextBeforeSequence">
/// Pass as <c>?beforeSequence=</c> to fetch the next (older) page. Null at the
/// start of the thread.
/// </param>
public sealed record ChatMessagePageResponse(
    IReadOnlyList<ChatMessageResponse> Messages,
    long? NextBeforeSequence,
    bool HasMore);

/// <param name="UpToSequence">
/// The newest sequence the user has seen. Clamped server-side to the thread's own
/// last sequence and never moved backwards, so a stale client cannot un-read a
/// thread.
/// </param>
public sealed record MarkConversationReadRequest(long UpToSequence);

public sealed record ConversationReadStateResponse(
    Guid ConversationId,
    string ParticipantType,
    Guid ParticipantId,
    long LastReadSequence,
    int UnreadCount,
    bool IsMuted);

/// <summary>
/// The result of clearing a chat for yourself
/// (<c>DELETE /conversations/{conversationId}</c>).
/// </summary>
/// <param name="ClearedUpToSequence">
/// Everything at or below this is now hidden from the caller — their history
/// starts again after it. The counterparty's copy is untouched; they still see
/// every message.
/// </param>
/// <param name="DeletedAtUtc">When it was cleared.</param>
/// <remarks>
/// The thread is NOT gone for good. It drops off the caller's inbox now, and
/// reappears the moment the counterparty sends something — carrying on from
/// there, without the cleared history. That is deliberate: the alternative is a
/// "delete" that silently stops you receiving messages.
/// </remarks>
public sealed record DeleteConversationResponse(
    Guid ConversationId,
    long ClearedUpToSequence,
    DateTimeOffset? DeletedAtUtc);

/// <summary>
/// The jobs behind a thread — <c>GET /conversations/{conversationId}/bookings</c>,
/// the chat screen's "View Jobs".
/// </summary>
/// <param name="Jobs">
/// Every booking of either kind between these two, newest first by service date.
/// <c>bookingType</c> discriminates ("SingleDay" | "NightStay") and the other
/// kind's fields are null — the same job-card shape the account and pet deletes
/// return when they are refused, so one card renders it everywhere.
/// </param>
/// <param name="TotalCount">All of the pair's jobs, not just this page.</param>
public sealed record ConversationJobsResponse(
    Guid ConversationId,
    Guid ProviderId,
    Guid PetParentId,
    IReadOnlyList<ParentOnboarding.PendingParentJobResponse> Jobs,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore);

/// <param name="UnreadConversationCount">
/// How many threads have anything unread — a different figure from the message
/// total, and the one "3 conversations need you" is built from.
/// </param>
public sealed record ChatUnreadSummaryResponse(
    int UnreadMessageCount,
    int UnreadConversationCount);

public sealed record BlockChatParticipantRequest(Guid CounterpartyId, string? Reason);

public sealed record ChatBlockResponse(
    Guid ChatBlockId,
    string BlockedType,
    Guid BlockedId,
    string? BlockedName,
    string? BlockedPhotoUrl,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

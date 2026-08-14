namespace Pawfront.Application.Support;

/// <summary>
/// Support tickets raised by one party against the other — "Report Incident" on a booking
/// and "Report Chat" on a conversation.
/// </summary>
/// <remarks>
/// <para>
/// Host-agnostic: both kinds and both directions go through here, and the caller supplies
/// which by passing a <see cref="SupportTicketTypes"/> and a
/// <see cref="SupportRaisedByTypes"/> value. That keeps the rules — party check, derived
/// counterparty, one open ticket per subject — in one place instead of four near-copies
/// across two hosts.
/// </para>
/// <para>
/// A ticket spans two stores. SQL owns the row: the parties, the subject, the status, and
/// therefore every rule that has to be a T-SQL predicate (the per-subject uniqueness, the
/// chat legal hold, the account and pet delete guards). Cosmos owns the narrative, which
/// can grow without bound as support and the creator go back and forth. This service is
/// what composes them.
/// </para>
/// <para>
/// <b>Raising a ticket does not block the reported party.</b> Nothing here writes to
/// <c>Chat.BlockedParticipants</c>: reporting is a message to support, and blocking stays
/// the user's own separate action.
/// </para>
/// </remarks>
public interface ISupportTicketService
{
    /// <summary>
    /// Raises a ticket against the counterparty on one booking or one conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answers <see cref="CreateSupportTicketOutcome.TicketAlreadyOpen"/> rather than
    /// throwing when that SUBJECT already has one open — the conflict carries the ticket
    /// the caller needs to name, the same posture <c>Provider.SetProviderActiveStatus</c>
    /// and <c>Provider.CreateClosures</c> take. Note the scope: another booking with the
    /// same counterparty is a separate incident and is reported separately.
    /// </para>
    /// <para>
    /// The SQL row is written FIRST and the narrative immediately after, because the
    /// ticket id is minted by the insert and the Cosmos document is keyed by it. A failure
    /// on the second leg is logged and swallowed: the ticket has committed, so failing the
    /// request would tell a reporter their report did not happen when it did — the precise
    /// failure mode the chat send was rebuilt to avoid. Support can ask for the detail
    /// through the clarification thread.
    /// </para>
    /// </remarks>
    /// <exception cref="SupportBookingNotFoundException">Unknown booking.</exception>
    /// <exception cref="SupportConversationNotFoundException">Unknown conversation.</exception>
    /// <exception cref="SupportForbiddenException">Caller is not a party to the subject.</exception>
    /// <exception cref="SupportNotAppBookingException">Custom walk-in.</exception>
    /// <exception cref="ArgumentException">Missing or oversized comment, or a malformed subject.</exception>
    Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Raises a BOOKING incident and attaches its evidence in ONE call — the combined
    /// form of <see cref="CreateAsync"/> + <see cref="AddPhotoAsync"/>, for a client that
    /// has the report and the photos in hand at the same moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two-call form exists because the blob path is keyed by the ticket's own id,
    /// which the insert is what mints — but that only forces an ordering, not two round
    /// trips, so this does both legs server-side.
    /// </para>
    /// <para>
    /// <b>Every photo is validated BEFORE the ticket is created.</b> That ordering is the
    /// whole point of the method: a caller who sent a sixth photo or an oversized file must
    /// be refused while that is still costless — not left with a raised report only half of
    /// their evidence reached.
    /// </para>
    /// <para>
    /// After the ticket exists, an individual photo failure is REPORTED, not thrown: the
    /// row has committed, so failing the request would misreport a report that did happen.
    /// The named casualties can be retried against
    /// <see cref="AddPhotoAsync"/>.
    /// </para>
    /// <para>
    /// On <see cref="CreateSupportTicketOutcome.TicketAlreadyOpen"/> <b>nothing is
    /// uploaded</b> — the returned ticket is one this call did not create, and writing
    /// evidence onto somebody else's open ticket would be wrong.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Chat incident (they carry no photos), too many photos, or a file that is empty,
    /// oversized, or of an unsupported type — all raised before anything is written.
    /// </exception>
    Task<CreateSupportTicketWithPhotosResult> CreateWithPhotosAsync(
        CreateSupportTicketCommand command,
        IReadOnlyList<SupportTicketPhotoUpload> photos,
        CancellationToken cancellationToken);

    /// <summary>
    /// One ticket with its narrative, scoped to a party to it. Null when the ticket is
    /// unknown OR the caller is not a party — deliberately the same answer, so a ticket id
    /// cannot be probed for existence.
    /// </summary>
    Task<SupportTicketDetail?> GetAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One party's tickets. Scoped by CALLER, not by direction: a ticket the counterparty
    /// raised against you is as much yours as one you raised, and both need answering.
    /// <c>take</c> is capped at <see cref="SupportTicketLimits.MaxPageSize"/>.
    /// </summary>
    /// <remarks>
    /// Returns rows only — no narrative. The list card shows the type, status and subject,
    /// all of which are on the row; fetching a document per card would be a fan-out for
    /// text the list does not render.
    /// </remarks>
    Task<SupportTicketListResult> ListAsync(
        SupportTicketQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attaches one uploaded photo to a booking incident, scoped to the ticket's CREATOR.
    /// </summary>
    /// <remarks>
    /// The evidence is the reporter's account of what happened, and the status vocabulary
    /// agrees — support asks the CREATOR for clarification and receives it from the
    /// CREATOR — so the counterparty is never in the evidence loop. There is deliberately
    /// no delete: evidence a reporter could retract after support has read it would defeat
    /// the point of the hold, which makes this the one gallery in the product with no
    /// delete path.
    /// </remarks>
    /// <exception cref="SupportTicketNotFoundException">Unknown ticket, or not this creator's.</exception>
    /// <exception cref="SupportTicketPhotoLimitReachedException">Already at the cap.</exception>
    /// <exception cref="SupportTicketPhotoNotBookingIncidentException">Chat incident.</exception>
    /// <exception cref="SupportTicketClosedException">Ticket is closed.</exception>
    Task<SupportTicketRecord> AddPhotoAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// The creator answers support's request for clarification, moving the ticket
    /// <c>CLARIFICATION_ASKED_TO_CREATOR</c> → <c>CLARIFICATION_RECEIVED_FROM_CREATOR</c>.
    /// This is the ONLY status transition either app can drive.
    /// </summary>
    /// <remarks>
    /// The reply text is appended to the Cosmos document BEFORE the status moves, and the
    /// ordering is deliberate: if the document write failed after the status had already
    /// moved, the ticket would claim an answer that was never recorded. This way a failure
    /// leaves the ticket in <c>ASKED</c> with the reply stored — visibly unfinished, and
    /// fixed by retrying.
    /// </remarks>
    /// <exception cref="SupportTicketNotFoundException">Unknown ticket, or not this creator's.</exception>
    /// <exception cref="SupportTicketClosedException">Ticket is closed.</exception>
    /// <exception cref="SupportNoClarificationRequestedException">Nothing was asked.</exception>
    /// <exception cref="ArgumentException">Missing or oversized reply.</exception>
    Task<SupportTicketDetail> RecordClarificationAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string reply,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader/writer behind <see cref="ISupportTicketService"/> — the ticket INDEX.
/// Validation and the page cap live in the service; this layer only moves rows.
/// </summary>
public interface ISupportTicketStore
{
    Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken);

    Task<SupportTicketRecord?> GetAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken);

    Task<SupportTicketListResult> ListAsync(
        SupportTicketQuery query,
        CancellationToken cancellationToken);

    Task<SupportTicketRecord> AddPhotoAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string photoUrl,
        CancellationToken cancellationToken);

    Task<SupportTicketRecord> RecordClarificationAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The Cosmos side of a ticket — what was said, by whom, in order.
/// </summary>
/// <remarks>
/// Its own interface rather than methods on <see cref="ISupportTicketStore"/> because the
/// two live in different infrastructure projects, exactly as <c>IChatConversationStore</c>
/// (SQL) and <c>IChatMessageStore</c> (Cosmos) do for the same split.
/// </remarks>
public interface ISupportTicketNarrativeStore
{
    /// <summary>
    /// Creates the document with the reporter's opening entry. Idempotent by ticket id, so
    /// a retry after a failed response cannot produce two opening reports.
    /// </summary>
    Task<SupportTicketNarrative> CreateAsync(
        Guid ticketId,
        string authorType,
        Guid authorId,
        string comment,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one entry. Returns the whole narrative, so a caller that has just written a
    /// clarification can render the thread without a second read.
    /// </summary>
    Task<SupportTicketNarrative> AppendAsync(
        Guid ticketId,
        string authorType,
        Guid? authorId,
        string kind,
        string text,
        CancellationToken cancellationToken);

    /// <summary>
    /// The narrative, or null when no document exists. Null is not an error — the document
    /// is written just after the row, so a ticket can briefly exist without one.
    /// </summary>
    Task<SupportTicketNarrative?> GetAsync(
        Guid ticketId,
        CancellationToken cancellationToken);
}

/// <summary>
/// "Is this conversation under legal hold, and if so by which ticket?"
/// </summary>
/// <remarks>
/// <para>
/// A separate, deliberately tiny interface because it is consumed by the CHAT host, which
/// has no other reason to know the support module exists. It exists at all because one of
/// the two chat delete paths cannot check the hold for itself: clearing a thread is a
/// T-SQL write and consults <c>Support.Tickets</c> inline, but retracting a MESSAGE is a
/// Cosmos document replace with no SQL statement in the path to hang the check on.
/// </para>
/// <para>
/// Reads only open tickets, served by the filtered index <c>IX_Tickets_OpenConversation</c>
/// — so on the overwhelmingly common free-thread path this is an index seek that finds
/// nothing.
/// </para>
/// </remarks>
public interface ISupportLegalHoldReader
{
    Task<ConversationLegalHold?> GetConversationHoldAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}

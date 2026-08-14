namespace Pawfront.Application.Support;

/// <summary>
/// Which kind of incident a ticket reports. Strings rather than an enum, matching the
/// rest of the Application layer's discriminators (<c>ReviewedBookingTypes</c>,
/// <c>BookingTypes</c>) and the <c>TicketType</c> column's CHECK constraint.
/// </summary>
public static class SupportTicketTypes
{
    /// <summary>"Report Incident" — raised against the counterparty on a booking.</summary>
    public const string BookingIncident = "BookingIncident";

    /// <summary>"Report Chat" — raised against the counterparty on a conversation.</summary>
    public const string ChatIncident = "ChatIncident";

    public static bool IsKnown(string? value) =>
        value is BookingIncident or ChatIncident;
}

/// <summary>
/// Which side raised a ticket. The same vocabulary <c>Chat.ConversationParticipants</c>
/// uses, deliberately — a chat incident is raised from a conversation, so a second
/// spelling would have to be translated at that boundary.
/// </summary>
public static class SupportRaisedByTypes
{
    public const string Provider = "Provider";
    public const string PetParent = "PetParent";

    public static bool IsKnown(string? value) =>
        value is Provider or PetParent;

    /// <summary>The other side. A ticket always runs provider ↔ parent.</summary>
    public static string Counterparty(string raisedByType) =>
        raisedByType == Provider ? PetParent : Provider;
}

/// <summary>
/// The ticket lifecycle, as literal uppercase strings — the same convention the booking
/// status engine uses, and the exact set <c>CK_Tickets_Status</c> admits.
/// </summary>
/// <remarks>
/// <para>
/// Only ONE of these transitions is reachable from the two app hosts:
/// <see cref="ClarificationAskedToCreator"/> → <see cref="ClarificationReceivedFromCreator"/>,
/// via <c>Support.RecordTicketClarification</c>. Every other move belongs to the admin
/// panel, which is why neither host exposes a general "set status" route.
/// </para>
/// <para>
/// <see cref="Closed"/> is the only terminal value, and the only one that stamps
/// <c>ClosedAtUtc</c> (<c>CK_Tickets_ClosedAtUtc</c> enforces both halves of that).
/// Everything else counts as OPEN, which is what the per-subject uniqueness indexes, the
/// chat legal hold and the account/pet delete guards all key off.
/// </para>
/// </remarks>
public static class SupportTicketStatuses
{
    public const string Opened = "OPENED";
    public const string InReview = "IN_REVIEW";
    public const string ClarificationAskedToCreator = "CLARIFICATION_ASKED_TO_CREATOR";
    public const string ClarificationReceivedFromCreator = "CLARIFICATION_RECEIVED_FROM_CREATOR";
    public const string PendingWithLegalTeam = "PENDING_WITH_LEGAL_TEAM";
    public const string Closed = "CLOSED";

    /// <summary>
    /// Every status a ticket can hold. Ordered as the lifecycle runs, so a client
    /// rendering a filter chip row gets a sensible order for free.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Opened,
        InReview,
        ClarificationAskedToCreator,
        ClarificationReceivedFromCreator,
        PendingWithLegalTeam,
        Closed
    ];

    /// <summary>
    /// Everything that is not <see cref="Closed"/>. This is the C#-side expansion of the
    /// friendly <c>Open</c> filter group; <c>Support.ListTickets</c> takes a plain CSV, so
    /// adding a status means editing this file and nothing else.
    /// </summary>
    public static readonly IReadOnlyList<string> Open =
    [
        Opened,
        InReview,
        ClarificationAskedToCreator,
        ClarificationReceivedFromCreator,
        PendingWithLegalTeam
    ];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// The bounds the support-ticket feature is built to. Kept together so endpoint
/// validation, the procedures' own defaults and the docs cannot drift apart.
/// </summary>
public static class SupportTicketLimits
{
    /// <summary>
    /// Per booking incident, and the figure <c>Support.AddTicketPhoto</c> enforces under
    /// <c>UPDLOCK + HOLDLOCK</c>. C# pre-checks it too, so a caller already at the cap is
    /// not charged an upload first — but SQL is the check that actually holds, because
    /// two uploads in flight would each read "room for one more".
    /// </summary>
    public const int MaxPhotos = 5;

    /// <summary>
    /// Page cap for the "my tickets" list, applied server-side and again by the
    /// procedure. Same figure the earnings, spend and review lists use, so a client
    /// learns one paging rule.
    /// </summary>
    public const int MaxPageSize = 20;

    /// <summary>
    /// The reporter's account of what happened. Generous because this is the substance of
    /// the report and the thing support reads first; it lives in the Cosmos document, so
    /// there is no column width to match.
    /// </summary>
    public const int MaxCommentLength = 4000;

    /// <summary>
    /// Matches <c>Support.Tickets.Category NVARCHAR(100)</c> — what KIND of problem the
    /// reporter picked. A plain string, not an enum: the vocabulary is the app's picker,
    /// so adding a category is a mobile release rather than a backend one.
    /// </summary>
    public const int MaxCategoryLength = 100;

    /// <summary>
    /// Matches <c>Support.Tickets.Reason NVARCHAR(500)</c>, the optional short reason
    /// stored alongside the free-text account that goes to the Cosmos narrative.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Per photo. Same 3 MB the review and evidence galleries use.
    /// </summary>
    /// <remarks>
    /// Lives here rather than only in the two endpoint classes because the combined
    /// report-with-photos flow validates uploads in the Application layer — it has to
    /// reject an oversized file BEFORE the ticket is created, and the endpoints and the
    /// service must agree on the limit to do that.
    /// </remarks>
    public const long MaxPhotoBytes = 3L * 1024 * 1024;

    /// <summary>What the incident galleries accept, matching every other upload here.</summary>
    public static readonly IReadOnlySet<string> AllowedPhotoContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/jpg", "image/png", "image/webp"
        };
}

/// <summary>Sort key for the "my tickets" list.</summary>
public enum SupportTicketSortBy
{
    /// <summary>Last activity — a new photo or a clarification moves it. The default.</summary>
    UpdatedAt = 0,

    /// <summary>When the ticket was raised.</summary>
    CreatedAt = 1
}

/// <summary>
/// One support ticket's SQL row — the index. The narrative (the reporter's comment and
/// the clarification thread) lives in the Cosmos <c>SupportTickets</c> document and is
/// carried separately by <see cref="SupportTicketDetail"/>.
/// </summary>
/// <param name="TicketNumber">
/// Raw sequential number; the caller formats the <c>TK-000123</c> label, exactly as the
/// booking reads format <c>PF-000123</c> from <c>JobNumber</c>.
/// </param>
/// <param name="RaisedByType">
/// Which side raised it. The list is scoped by party rather than by direction — a ticket
/// the counterparty raised against you is as much yours as one you raised — so this is
/// what lets the card say which way round it is.
/// </param>
/// <param name="Category">
/// How the reporter classified the incident — what kind of problem it is. Optional, and a
/// plain string rather than an enum: the vocabulary lives in the app's picker.
/// </param>
/// <param name="Reason">
/// The reporter's one-line summary of this particular incident. Optional. The substance
/// is the narrative's opening entry, in Cosmos; these two are the filing labels beside it.
/// </param>
public sealed record SupportTicketRecord(
    Guid TicketId,
    int TicketNumber,
    string TicketType,
    Guid ProviderId,
    Guid PetParentId,
    string RaisedByType,
    string? BookingType,
    Guid? BookingId,
    Guid? PetId,
    Guid? ConversationId,
    string Status,
    string? Category,
    string? Reason,
    IReadOnlyList<SupportTicketPhotoRecord> Photos,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ClosedAtUtc)
{
    /// <summary>
    /// True for everything but <c>CLOSED</c>. This is the predicate the per-subject
    /// uniqueness, the chat legal hold and the delete guards all turn on, so it is named
    /// once here.
    /// </summary>
    public bool IsOpen => !string.Equals(Status, SupportTicketStatuses.Closed, StringComparison.Ordinal);

    /// <summary>Whether this ticket was raised by the given party.</summary>
    public bool WasRaisedBy(string actorType) =>
        string.Equals(RaisedByType, actorType, StringComparison.Ordinal);
}

public sealed record SupportTicketPhotoRecord(
    Guid TicketPhotoId,
    Guid TicketId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A ticket with its narrative attached — the SQL row plus the Cosmos document's entries.
/// </summary>
/// <remarks>
/// <see cref="Narrative"/> is null when the document could not be read. That is not
/// necessarily an error: the document is written immediately AFTER the row on the create
/// path, so a ticket can legitimately exist for a moment without one, and a Cosmos outage
/// should degrade the detail rather than fail it. The row is what proves the ticket
/// exists and who may see it.
/// </remarks>
public sealed record SupportTicketDetail(
    SupportTicketRecord Ticket,
    SupportTicketNarrative? Narrative);

/// <summary>
/// The Cosmos side of a ticket: what was said, by whom, in order. Unbounded by design —
/// support and the creator can go back and forth — which is exactly why it is not in SQL.
/// </summary>
public sealed record SupportTicketNarrative(
    Guid TicketId,
    IReadOnlyList<SupportTicketNarrativeEntry> Entries,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// One entry in a ticket's narrative, oldest first. The opening entry is always the
/// reporter's own <see cref="SupportNarrativeEntryKinds.Report"/>.
/// </summary>
/// <param name="AuthorType">
/// <c>Provider</c>, <c>PetParent</c> or <c>Support</c>. Support is not a party to the
/// ticket in SQL — it has no id in this product — so its entries carry a null
/// <paramref name="AuthorId"/>.
/// </param>
public sealed record SupportTicketNarrativeEntry(
    Guid EntryId,
    string AuthorType,
    Guid? AuthorId,
    string Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Who wrote a narrative entry. Extends the two party types with <see cref="Support"/>,
/// which exists only in the document — the admin panel has no row in this database.
/// </summary>
public static class SupportNarrativeAuthorTypes
{
    public const string Provider = "Provider";
    public const string PetParent = "PetParent";
    public const string Support = "Support";
}

/// <summary>What a narrative entry is.</summary>
public static class SupportNarrativeEntryKinds
{
    /// <summary>The opening report. Exactly one per ticket, written at creation.</summary>
    public const string Report = "Report";

    /// <summary>Support asking the creator for more detail.</summary>
    public const string ClarificationRequest = "ClarificationRequest";

    /// <summary>The creator answering that request.</summary>
    public const string ClarificationReply = "ClarificationReply";

    /// <summary>An internal or closing note from support.</summary>
    public const string Note = "Note";
}

/// <summary>
/// Raise a ticket. <paramref name="ActorId"/> is the caller's own id — their ProviderId or
/// PetParentId — and is taken from the authenticated route, never from a request body.
/// The counterparty is derived by <c>Support.CreateTicket</c> from the subject, so a
/// report cannot be filed against somebody who was never party to it.
/// </summary>
/// <param name="Comment">
/// The reporter's account of what happened. Goes to the Cosmos narrative as the opening
/// entry, not to SQL.
/// </param>
/// <param name="Category">
/// Optional. What kind of problem it is, as the app's picker labelled it.
/// </param>
/// <param name="Reason">
/// Optional short reason for this particular incident. Stored on the ticket row beside
/// <paramref name="Category"/>; the substance is <paramref name="Comment"/>.
/// </param>
public sealed record CreateSupportTicketCommand(
    string TicketType,
    string RaisedByType,
    Guid ActorId,
    string? BookingType,
    Guid? BookingId,
    Guid? ConversationId,
    string Comment,
    string? Category,
    string? Reason);

/// <summary>
/// What <c>Support.CreateTicket</c> decided. <see cref="Created"/> means the ticket was
/// written; <see cref="TicketAlreadyOpen"/> means this BOOKING (or conversation) already
/// has an open ticket and NOTHING was written — the ticket carried back is the one already
/// open, so the caller can answer 409 naming it rather than leaving the reporter at a
/// dead end.
/// </summary>
/// <remarks>
/// Scoped to the subject, not to the pair: a parent with several bookings from one
/// provider can report each of them, since each is a separate incident. What is refused is
/// a second open report of the SAME booking, from either side.
/// </remarks>
public enum CreateSupportTicketOutcome
{
    Created = 0,
    TicketAlreadyOpen = 1
}

/// <summary>The result of raising a ticket, discriminated by <see cref="Outcome"/>.</summary>
public sealed record CreateSupportTicketResult(
    CreateSupportTicketOutcome Outcome,
    SupportTicketRecord Ticket);

/// <summary>
/// One evidence photo supplied alongside a report, already read off the request.
/// </summary>
/// <remarks>
/// Deliberately NOT <c>IFormFile</c>: no Application type takes one anywhere in this
/// codebase — uploads are read at the endpoint and handed down as a stream — and keeping
/// that boundary is what lets this flow be driven from a test without an HTTP context.
/// <see cref="Content"/> is read once, so it is the caller's stream to dispose.
/// </remarks>
public sealed record SupportTicketPhotoUpload(
    string FileName,
    string ContentType,
    long Length,
    Stream Content);

/// <summary>
/// One photo that did not make it onto the ticket, and why.
/// </summary>
/// <remarks>
/// A failure here never fails the request: by the time photos are uploaded the ticket
/// row has committed, so refusing would tell a reporter their report did not happen when
/// it did. Reporting the casualty by name instead lets the client
/// retry just that file against <c>POST .../{ticketId}/photos</c> — which is why the
/// per-photo endpoint stays.
/// </remarks>
public sealed record SupportTicketPhotoFailure(
    string FileName,
    string Code,
    string Message);

/// <summary>
/// The result of raising a ticket AND attaching its evidence in one call.
/// </summary>
/// <param name="PhotoFailures">
/// Empty on the happy path. Non-empty means the ticket exists, but the named files are
/// not on it.
/// </param>
public sealed record CreateSupportTicketWithPhotosResult(
    CreateSupportTicketOutcome Outcome,
    SupportTicketRecord Ticket,
    IReadOnlyList<SupportTicketPhotoFailure> PhotoFailures);

/// <summary>Query for one party's "my tickets" list.</summary>
/// <param name="Statuses">
/// Already expanded to raw lifecycle statuses; null or empty means every status. The
/// friendly <c>Open</c> / <c>Closed</c> groups are expanded in
/// <see cref="SupportTicketQueryParsing"/>, so the procedure never learns about groups.
/// </param>
public sealed record SupportTicketQuery(
    string ActorType,
    Guid ActorId,
    IReadOnlyList<string>? Statuses,
    SupportTicketSortBy SortBy,
    Earnings.EarningsSortDirection SortDirection,
    int Skip,
    int Take);

/// <summary>A page of one party's tickets, with the whole-population total.</summary>
public sealed record SupportTicketListResult(
    IReadOnlyList<SupportTicketRecord> Items,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasMore => Skip + Items.Count < TotalCount;
}

/// <summary>
/// An open ticket that refused a delete. Returned as the last result set of
/// <c>Parent.DeletePetParent</c>, <c>Parent.DeletePetParentPet</c> and
/// <c>Provider.DeleteProvider</c>, which deliberately project the SAME columns in the
/// SAME order so one reader serves all three.
/// </summary>
/// <remarks>
/// Deliberately slimmer than <see cref="SupportTicketRecord"/>: a refusal only needs to
/// say which tickets are in the way, and the caller can open any of them by id for the
/// rest. It carries no party ids for the same reason — whoever is being told already
/// knows they are one of the two.
/// </remarks>
public sealed record BlockingSupportTicket(
    Guid TicketId,
    int TicketNumber,
    string TicketType,
    string RaisedByType,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// An open ticket holding a conversation. Returned by
/// <c>Support.GetConversationLegalHold</c>; absence of a row means the thread is free.
/// </summary>
/// <remarks>
/// Carries the ticket number so a refusal can NAME it — "TK-000123 is open on this chat"
/// is actionable in a way "you cannot delete this" is not.
/// </remarks>
public sealed record ConversationLegalHold(
    Guid TicketId,
    int TicketNumber,
    string TicketType,
    string Status);

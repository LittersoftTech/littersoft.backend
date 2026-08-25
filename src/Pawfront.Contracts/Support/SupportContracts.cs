namespace Pawfront.Contracts.Support;

/// <summary>
/// Body for "Report Incident" — <c>POST .../support-tickets/report-incident</c> on either
/// host. The reporter names only the booking and what happened; the counterparty is
/// derived server-side from the booking, so it cannot be aimed at somebody who was never
/// party to it.
/// </summary>
/// <param name="BookingType"><c>SingleDay</c> or <c>NightStay</c>, case-insensitive.</param>
/// <param name="Comment">
/// What happened, in the reporter's own words. Required — it is the substance of the
/// report and the first thing support reads.
/// </param>
/// <param name="Category">
/// Optional. What KIND of problem this is, as the app's picker labelled it — a plain
/// string, so the server never has to be released to add a category.
/// </param>
/// <param name="Reason">
/// Optional short reason for this particular incident, recorded on the ticket beside the
/// category. Never shown to the reported party.
/// </param>
public sealed record ReportBookingIncidentRequest(
    string BookingType,
    Guid BookingId,
    string Comment,
    string? Category = null,
    string? Reason = null);

/// <summary>
/// Response for the combined <c>POST .../support-tickets/report-incident-with-photos</c>,
/// which raises the ticket and attaches its evidence in one call.
/// </summary>
/// <param name="Ticket">
/// The ticket as created, with whatever photos landed — read <c>photos</c> there for the
/// stored URLs.
/// </param>
/// <param name="AttachedPhotoCount">How many of the supplied files are on the ticket.</param>
/// <param name="PhotoErrors">
/// Empty on the happy path. Non-empty means the ticket EXISTS, but these files are not
/// attached — retry just those against
/// <c>POST .../{ticketId}/photos</c>. The report is never failed over a photo, because by
/// then the ticket has committed and saying otherwise would misreport it.
/// </param>
public sealed record CreateSupportTicketWithPhotosResponse(
    SupportTicketResponse Ticket,
    int AttachedPhotoCount,
    IReadOnlyList<SupportTicketPhotoErrorResponse> PhotoErrors);

/// <summary>One supplied file that did not make it onto the ticket.</summary>
public sealed record SupportTicketPhotoErrorResponse(
    string FileName,
    string Code,
    string Message);

/// <summary>
/// Body for "Report an app issue" — <c>POST .../support-tickets/report-app-issue</c> on
/// either host. Nothing is named but the problem: an app issue has no subject and no
/// counterparty, so the ticket records only its reporter.
/// </summary>
/// <param name="Comment">
/// What went wrong, in the reporter's own words. <b>Optional here</b>, unlike a report
/// against a person: when it is absent <paramref name="Reason"/> opens the ticket's
/// narrative instead. Sending neither is a 400 — there would be nothing to report.
/// </param>
/// <remarks>
/// Unlimited: an app issue has no subject to key a "one open ticket" rule on, and each bug
/// report is a different bug.
/// </remarks>
public sealed record ReportAppIssueRequest(
    string? Comment = null,
    string? Category = null,
    string? Reason = null);

/// <summary>
/// Body for "Report an event" — <c>POST .../support-tickets/report-event</c> on either
/// host.
/// </summary>
/// <param name="Comment">
/// Optional, exactly as on <see cref="ReportAppIssueRequest"/> — <paramref name="Reason"/>
/// stands in for it when absent.
/// </param>
/// <remarks>
/// The organiser is NOT recorded as a counterparty and is never told. An event is public,
/// so there is no attendance check either: the only refusal is 404 <c>EventNotFound</c>.
/// One open ticket per event <i>per reporter</i> — a second attendee reporting the same
/// event is a separate account of it and gets a separate ticket.
/// </remarks>
public sealed record ReportEventIncidentRequest(
    Guid EventId,
    string? Comment = null,
    string? Category = null,
    string? Reason = null);

/// <summary>
/// Body for "Report Chat" — <c>POST .../support-tickets/report-chat</c> on either host.
/// The counterparty is derived from the conversation.
/// </summary>
/// <remarks>
/// No message id: the whole thread goes under legal hold, so support reads it in context
/// rather than being pointed at one line. That is also why a chat incident carries no
/// photos — the images already in the thread are the evidence.
/// </remarks>
public sealed record ReportChatIncidentRequest(
    Guid ConversationId,
    string Comment,
    string? Category = null,
    string? Reason = null);

/// <summary>
/// Body for the creator's answer to support's request for clarification —
/// <c>POST .../support-tickets/{ticketId}/clarification</c>.
/// </summary>
public sealed record SubmitTicketClarificationRequest(string Reply);

/// <summary>
/// A support ticket as either party sees it.
/// </summary>
/// <param name="TicketRef">
/// The friendly reference shown to both parties and to support, <c>TK-000123</c> — the same
/// idea as a booking's <c>jobId</c> (<c>PF-000123</c>) and a payout's <c>PO-000123</c>.
/// <see cref="TicketId"/> stays the GUID the routes take.
/// </param>
/// <param name="TicketType">
/// <c>BookingIncident</c>, <c>ChatIncident</c>, <c>EventIncident</c> or <c>AppIssue</c> —
/// branch on this to know which subject field is populated.
/// </param>
/// <param name="ProviderId">
/// Null when the ticket has no counterparty — an <c>AppIssue</c> or <c>EventIncident</c>
/// raised by a pet parent records only their side. The two counterparty kinds always carry
/// both.
/// </param>
/// <param name="PetParentId">The mirror of <paramref name="ProviderId"/>.</param>
/// <param name="EventId">The reported event; set on <c>EventIncident</c> only.</param>
/// <param name="RaisedByType">
/// Which side raised it. The list is scoped by party rather than by direction — a ticket
/// raised against you is as much yours as one you raised — so this is what lets the card
/// say which way round it is. <see cref="RaisedByMe"/> is the same fact pre-computed for
/// the caller.
/// </param>
/// <param name="IsOpen">
/// Everything but <c>CLOSED</c>. This is the flag that governs what the app may offer: an
/// open ticket holds the reported chat, refuses a second report of the same booking or
/// conversation, and blocks the account and pet deletes.
/// </param>
/// <param name="CanSubmitClarification">
/// True only when support has asked THIS caller for clarification and the ticket is open —
/// i.e. they raised it and it sits in <c>CLARIFICATION_ASKED_TO_CREATOR</c>. It is the one
/// status transition either app can drive.
/// </param>
/// <param name="Category">
/// How the reporter classified the incident, or null if they classified nothing. Echoed
/// back exactly as it was sent — the server neither validates nor translates the value.
/// </param>
/// <param name="Reason">
/// The reporter's one-line summary, or null. Visible to both parties on their own tickets;
/// the reported party sees it only because the ticket is theirs too, and it is never
/// surfaced anywhere else.
/// </param>
public sealed record SupportTicketResponse(
    Guid TicketId,
    string TicketRef,
    string TicketType,
    Guid? ProviderId,
    Guid? PetParentId,
    string RaisedByType,
    bool RaisedByMe,
    string? BookingType,
    Guid? BookingId,
    Guid? PetId,
    Guid? ConversationId,
    Guid? EventId,
    string Status,
    string? Category,
    string? Reason,
    bool IsOpen,
    bool CanSubmitClarification,
    IReadOnlyList<SupportTicketPhotoResponse> Photos,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record SupportTicketPhotoResponse(
    Guid TicketPhotoId,
    Guid TicketId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A ticket with its narrative — the thread of what was said about it.
/// </summary>
/// <param name="Entries">
/// Oldest first, opening with the reporter's own account. <b>Empty rather than absent</b>
/// when the narrative could not be read: the ticket itself is still real, and a support
/// screen should degrade to showing the ticket rather than failing outright.
/// </param>
public sealed record SupportTicketDetailResponse(
    SupportTicketResponse Ticket,
    IReadOnlyList<SupportTicketNarrativeEntryResponse> Entries);

/// <summary>One entry in a ticket's narrative.</summary>
/// <param name="AuthorType">
/// <c>Provider</c>, <c>PetParent</c> or <c>Support</c>. Support is not a party to the
/// ticket in this database, so its entries carry a null <paramref name="AuthorId"/>.
/// </param>
/// <param name="Kind">
/// <c>Report</c> (the opening account), <c>ClarificationRequest</c> (support asking),
/// <c>ClarificationReply</c> (the creator answering) or <c>Note</c>.
/// </param>
public sealed record SupportTicketNarrativeEntryResponse(
    Guid EntryId,
    string AuthorType,
    Guid? AuthorId,
    bool IsMine,
    string Kind,
    string Text,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// One open ticket standing in the way of a delete, as returned in the 409 body of the
/// account and pet deletes.
/// </summary>
/// <remarks>
/// Deliberately slimmer than <see cref="SupportTicketResponse"/> — a refusal only needs to
/// say which tickets are in the way, and the caller can open any of them by id for the
/// rest.
/// </remarks>
public sealed record BlockingSupportTicketResponse(
    Guid TicketId,
    string TicketRef,
    string TicketType,
    string RaisedByType,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 409 <c>OpenTicketsExist</c> body for the pet-parent account delete. Nothing was
/// changed — the account is untouched.
/// </summary>
/// <remarks>
/// Unlike the pending-jobs refusal, the parent cannot clear this themselves: only support
/// closing the ticket lifts it. There is no force override, matching every other refusal
/// in this codebase.
/// </remarks>
public sealed record OpenTicketsForPetParentResponse(
    Guid PetParentId,
    IReadOnlyList<BlockingSupportTicketResponse> OpenTickets);

/// <summary>409 <c>OpenTicketsExist</c> body for the per-pet delete.</summary>
public sealed record OpenTicketsForPetResponse(
    Guid PetId,
    IReadOnlyList<BlockingSupportTicketResponse> OpenTickets);

/// <summary>409 <c>OpenTicketsExist</c> body for the provider account delete.</summary>
public sealed record OpenTicketsForProviderResponse(
    Guid ProviderId,
    IReadOnlyList<BlockingSupportTicketResponse> OpenTickets);

/// <summary>A page of one party's tickets.</summary>
public sealed record SupportTicketListResponse(
    IReadOnlyList<SupportTicketResponse> Tickets,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore);

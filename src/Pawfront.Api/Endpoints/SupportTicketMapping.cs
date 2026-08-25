using Pawfront.Application.Support;
using Pawfront.Contracts.Support;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Application records → wire contracts for support tickets.
/// </summary>
/// <remarks>
/// Duplicated on the pet-parent host, exactly as <c>ReviewResponseMapping</c> is: the two
/// hosts are deliberately independent and share only the Application layer. The one thing
/// that MUST stay in step is the caller-relative flags — <c>raisedByMe</c>,
/// <c>isMine</c> and <c>canSubmitClarification</c> — since a ticket looks different
/// depending on which side is asking.
/// </remarks>
internal static class SupportTicketMapping
{
    public static SupportTicketResponse ToResponse(SupportTicketRecord ticket, string actorType)
    {
        var raisedByMe = ticket.WasRaisedBy(actorType);

        return new SupportTicketResponse(
            TicketId: ticket.TicketId,
            // Same D6 formatting as a booking's PF-000123 and a payout's PO-000123, so
            // every friendly reference in the product reads alike.
            TicketRef: $"TK-{ticket.TicketNumber:D6}",
            TicketType: ticket.TicketType,
            ProviderId: ticket.ProviderId,
            PetParentId: ticket.PetParentId,
            RaisedByType: ticket.RaisedByType,
            RaisedByMe: raisedByMe,
            BookingType: ticket.BookingType,
            BookingId: ticket.BookingId,
            PetId: ticket.PetId,
            ConversationId: ticket.ConversationId,
            EventId: ticket.EventId,
            Status: ticket.Status,
            Category: ticket.Category,
            Reason: ticket.Reason,
            IsOpen: ticket.IsOpen,
            // Only the CREATOR is ever asked, so the counterparty never sees this true —
            // which mirrors Support.RecordTicketClarification's own scoping rather than
            // relying on the app to hide the button.
            CanSubmitClarification: raisedByMe
                && ticket.IsOpen
                && string.Equals(
                    ticket.Status,
                    SupportTicketStatuses.ClarificationAskedToCreator,
                    StringComparison.Ordinal),
            Photos: [.. ticket.Photos.Select(ToResponse)],
            CreatedAtUtc: ticket.CreatedAtUtc,
            UpdatedAtUtc: ticket.UpdatedAtUtc,
            ClosedAtUtc: ticket.ClosedAtUtc);
    }

    public static SupportTicketPhotoResponse ToResponse(SupportTicketPhotoRecord photo) =>
        new(photo.TicketPhotoId, photo.TicketId, photo.PhotoUrl, photo.CreatedAtUtc);

    public static SupportTicketDetailResponse ToDetailResponse(
        SupportTicketDetail detail,
        string actorType,
        Guid actorId)
    {
        // An unreadable narrative degrades to an empty thread rather than failing the
        // screen — the ticket is what proves the report exists.
        var entries = detail.Narrative?.Entries ?? [];

        return new SupportTicketDetailResponse(
            ToResponse(detail.Ticket, actorType),
            [.. entries.Select(entry => ToResponse(entry, actorType, actorId))]);
    }

    public static SupportTicketNarrativeEntryResponse ToResponse(
        SupportTicketNarrativeEntry entry,
        string actorType,
        Guid actorId) =>
        new(
            EntryId: entry.EntryId,
            AuthorType: entry.AuthorType,
            AuthorId: entry.AuthorId,
            // Both halves matter: an entry is the caller's only when the SIDE and the id
            // agree. Comparing ids alone would make a Support entry (null id) ambiguous.
            IsMine: string.Equals(entry.AuthorType, actorType, StringComparison.Ordinal)
                && entry.AuthorId == actorId,
            Kind: entry.Kind,
            Text: entry.Text,
            CreatedAtUtc: entry.CreatedAtUtc);

    /// <summary>
    /// One open ticket blocking a delete. Shared by the account and pet refusals, which
    /// all return the identical shape.
    /// </summary>
    public static BlockingSupportTicketResponse ToResponse(BlockingSupportTicket ticket) =>
        new(
            TicketId: ticket.TicketId,
            TicketRef: $"TK-{ticket.TicketNumber:D6}",
            TicketType: ticket.TicketType,
            RaisedByType: ticket.RaisedByType,
            Status: ticket.Status,
            CreatedAtUtc: ticket.CreatedAtUtc);

    public static SupportTicketListResponse ToListResponse(
        SupportTicketListResult page,
        string actorType) =>
        new(
            Tickets: [.. page.Items.Select(ticket => ToResponse(ticket, actorType))],
            TotalCount: page.TotalCount,
            Skip: page.Skip,
            Take: page.Take,
            HasMore: page.HasMore);
}

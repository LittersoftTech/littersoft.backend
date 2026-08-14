using System.Collections.Concurrent;
using Pawfront.Application.Earnings;
using Pawfront.Application.Support;

namespace Pawfront.Infrastructure.Sql.Support;

/// <summary>
/// Dictionary-backed support tickets for the in-memory development configuration.
/// </summary>
/// <remarks>
/// <para>
/// Functional rather than a zeros-reporting Null store, for the reason
/// <c>InMemoryBookingReviewStore</c> is: this is a write flow, so a store that accepted a
/// report and never showed it again would make the feature untestable without SQL.
/// </para>
/// <para>
/// Two things it CANNOT do, both because it cannot see the other tables: derive the
/// counterparty from a booking or conversation (the caller's own id is used for their side
/// and the counterparty is left empty), and enforce the party check. The
/// one-open-ticket-per-subject rule and the photo cap ARE enforced, since both are
/// answerable from what is held here.
/// </para>
/// </remarks>
internal sealed class InMemorySupportTicketStore : ISupportTicketStore, ISupportLegalHoldReader
{
    private readonly ConcurrentDictionary<Guid, SupportTicketRecord> tickets = new();
    private readonly object gate = new();
    private int nextTicketNumber;

    public Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken)
    {
        var providerId = command.RaisedByType == SupportRaisedByTypes.Provider
            ? command.ActorId
            : Guid.Empty;
        var petParentId = command.RaisedByType == SupportRaisedByTypes.PetParent
            ? command.ActorId
            : Guid.Empty;

        lock (gate)
        {
            // Scoped to the SUBJECT, matching UX_Tickets_OpenBooking /
            // UX_Tickets_OpenConversation: two bookings with the same provider are two
            // incidents and get two tickets, but one booking cannot be reported twice
            // while the first report is open.
            var existing = tickets.Values.FirstOrDefault(ticket =>
                ticket.IsOpen
                && (command.BookingId is { } bookingId
                    ? ticket.BookingId == bookingId
                        && string.Equals(ticket.BookingType, command.BookingType, StringComparison.Ordinal)
                    : ticket.ConversationId == command.ConversationId));

            if (existing is not null)
            {
                return Task.FromResult(
                    new CreateSupportTicketResult(CreateSupportTicketOutcome.TicketAlreadyOpen, existing));
            }

            var now = DateTimeOffset.UtcNow;
            var record = new SupportTicketRecord(
                TicketId: Guid.NewGuid(),
                TicketNumber: Interlocked.Increment(ref nextTicketNumber),
                TicketType: command.TicketType,
                ProviderId: providerId,
                PetParentId: petParentId,
                RaisedByType: command.RaisedByType,
                BookingType: command.BookingType,
                BookingId: command.BookingId,
                PetId: null,
                ConversationId: command.ConversationId,
                Status: SupportTicketStatuses.Opened,
                Category: command.Category,
                Reason: command.Reason,
                Photos: [],
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                ClosedAtUtc: null);

            tickets[record.TicketId] = record;

            return Task.FromResult(
                new CreateSupportTicketResult(CreateSupportTicketOutcome.Created, record));
        }
    }

    public Task<SupportTicketRecord?> GetAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (!tickets.TryGetValue(ticketId, out var ticket) || !IsParty(ticket, actorType, actorId))
        {
            return Task.FromResult<SupportTicketRecord?>(null);
        }

        return Task.FromResult<SupportTicketRecord?>(ticket);
    }

    public Task<SupportTicketListResult> ListAsync(
        SupportTicketQuery query,
        CancellationToken cancellationToken)
    {
        var matching = tickets.Values
            .Where(ticket => IsParty(ticket, query.ActorType, query.ActorId))
            .Where(ticket => query.Statuses is not { Count: > 0 }
                || query.Statuses.Contains(ticket.Status, StringComparer.Ordinal))
            .ToList();

        var ordered = (query.SortBy, query.SortDirection) switch
        {
            (SupportTicketSortBy.CreatedAt, EarningsSortDirection.Ascending) =>
                matching.OrderBy(ticket => ticket.CreatedAtUtc).ThenBy(ticket => ticket.TicketId),
            (SupportTicketSortBy.CreatedAt, _) =>
                matching.OrderByDescending(ticket => ticket.CreatedAtUtc).ThenBy(ticket => ticket.TicketId),
            (_, EarningsSortDirection.Ascending) =>
                matching.OrderBy(ticket => ticket.UpdatedAtUtc).ThenBy(ticket => ticket.TicketId),
            _ =>
                matching.OrderByDescending(ticket => ticket.UpdatedAtUtc).ThenBy(ticket => ticket.TicketId)
        };

        var page = ordered.Skip(query.Skip).Take(query.Take).ToArray();

        return Task.FromResult(
            new SupportTicketListResult(page, matching.Count, query.Skip, query.Take));
    }

    public Task<SupportTicketRecord> AddPhotoAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var ticket = FindForCreator(ticketId, actorType, actorId);

            if (ticket.TicketType != SupportTicketTypes.BookingIncident)
            {
                throw new SupportTicketPhotoNotBookingIncidentException(ticketId);
            }

            if (!ticket.IsOpen)
            {
                throw new SupportTicketClosedException(ticketId);
            }

            if (ticket.Photos.Count >= SupportTicketLimits.MaxPhotos)
            {
                throw new SupportTicketPhotoLimitReachedException(ticketId, SupportTicketLimits.MaxPhotos);
            }

            var now = DateTimeOffset.UtcNow;
            var updated = ticket with
            {
                Photos =
                [
                    .. ticket.Photos,
                    new SupportTicketPhotoRecord(Guid.NewGuid(), ticketId, photoUrl, now)
                ],
                UpdatedAtUtc = now
            };

            tickets[ticketId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<SupportTicketRecord> RecordClarificationAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var ticket = FindForCreator(ticketId, actorType, actorId);

            if (!ticket.IsOpen)
            {
                throw new SupportTicketClosedException(ticketId);
            }

            if (!string.Equals(
                    ticket.Status,
                    SupportTicketStatuses.ClarificationAskedToCreator,
                    StringComparison.Ordinal))
            {
                throw new SupportNoClarificationRequestedException(ticketId);
            }

            var updated = ticket with
            {
                Status = SupportTicketStatuses.ClarificationReceivedFromCreator,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            tickets[ticketId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ConversationLegalHold?> GetConversationHoldAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var holding = tickets.Values
            .Where(ticket => ticket.ConversationId == conversationId && ticket.IsOpen)
            .OrderBy(ticket => ticket.CreatedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(holding is null
            ? null
            : new ConversationLegalHold(
                holding.TicketId, holding.TicketNumber, holding.TicketType, holding.Status));
    }

    /// <summary>
    /// Unknown ticket and "not the one you raised" are one answer here too, matching the
    /// procedures — the two must not be distinguishable in either configuration.
    /// </summary>
    private SupportTicketRecord FindForCreator(Guid ticketId, string actorType, Guid actorId)
    {
        if (!tickets.TryGetValue(ticketId, out var ticket)
            || !IsParty(ticket, actorType, actorId)
            || !ticket.WasRaisedBy(actorType))
        {
            throw new SupportTicketNotFoundException(ticketId);
        }

        return ticket;
    }

    private static bool IsParty(SupportTicketRecord ticket, string actorType, Guid actorId) =>
        actorType == SupportRaisedByTypes.Provider
            ? ticket.ProviderId == actorId
            : ticket.PetParentId == actorId;
}

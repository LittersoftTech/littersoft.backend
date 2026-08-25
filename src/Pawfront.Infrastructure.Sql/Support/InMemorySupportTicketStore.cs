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
/// Three things it CANNOT do, all because it cannot see the other tables: derive the
/// counterparty from a booking or conversation (the caller's own id is used for their side
/// and the counterparty is left empty), enforce the party check, and check that a reported
/// event exists. The one-open-ticket-per-subject rules and the photo cap ARE enforced,
/// since all of them are answerable from what is held here.
/// </para>
/// </remarks>
internal sealed class InMemorySupportTicketStore
    : ISupportTicketStore, ISupportLegalHoldReader, IMySupportTicketLookup
{
    private readonly ConcurrentDictionary<Guid, SupportTicketRecord> tickets = new();
    private readonly object gate = new();
    private int nextTicketNumber;

    public Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken)
    {
        var raisedByProvider = command.RaisedByType == SupportRaisedByTypes.Provider;

        // A counterparty kind stores both sides — the caller's own id for theirs and an
        // empty id for the other, since this store cannot see the booking or conversation
        // to derive it. A reporter-only kind stores just the caller's, which IS the real
        // shape and needs no apology.
        var hasCounterparty = SupportTicketTypes.HasCounterparty(command.TicketType);
        var providerId = raisedByProvider ? command.ActorId
            : hasCounterparty ? Guid.Empty : (Guid?)null;
        var petParentId = !raisedByProvider ? command.ActorId
            : hasCounterparty ? Guid.Empty : (Guid?)null;

        lock (gate)
        {
            // Scoped to the SUBJECT, matching UX_Tickets_OpenBooking /
            // UX_Tickets_OpenConversation / UX_Tickets_OpenEventReporter: two bookings with
            // the same provider are two incidents and get two tickets, but one booking
            // cannot be reported twice while the first report is open. An event is scoped
            // per reporter as well, and an app issue is never refused.
            var existing = FindOpenForSubject(command);

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
                EventId: command.EventId,
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

            if (!SupportTicketTypes.AllowsPhotos(ticket.TicketType))
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

    public Task<MySupportTicketSubjects> GetAsync(
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var mine = tickets.Values
            .Where(ticket => ticket.IsOpen
                && ticket.WasRaisedBy(actorType)
                && IsParty(ticket, actorType, actorId))
            .OrderBy(ticket => ticket.CreatedAtUtc)
            .Select(ticket => new MySupportTicketSubject(
                ticket.TicketId,
                ticket.TicketNumber,
                ticket.TicketType,
                ticket.BookingType,
                ticket.BookingId,
                ticket.ConversationId,
                ticket.EventId));

        return Task.FromResult(new MySupportTicketSubjects(mine));
    }

    /// <summary>
    /// The open ticket already covering this report's subject, or null — the in-memory
    /// mirror of the three filtered-unique indexes. An app issue has no subject and so
    /// always returns null.
    /// </summary>
    private SupportTicketRecord? FindOpenForSubject(CreateSupportTicketCommand command)
    {
        var open = tickets.Values.Where(ticket => ticket.IsOpen);

        return command.TicketType switch
        {
            SupportTicketTypes.BookingIncident => open.FirstOrDefault(ticket =>
                ticket.BookingId == command.BookingId
                && string.Equals(ticket.BookingType, command.BookingType, StringComparison.Ordinal)),

            SupportTicketTypes.ChatIncident => open.FirstOrDefault(ticket =>
                ticket.ConversationId == command.ConversationId),

            // Per REPORTER as well as per event: another attendee's report of the same
            // event is a separate account and gets its own ticket.
            SupportTicketTypes.EventIncident => open.FirstOrDefault(ticket =>
                ticket.EventId == command.EventId
                && ticket.WasRaisedBy(command.RaisedByType)
                && IsParty(ticket, command.RaisedByType, command.ActorId)),

            _ => null
        };
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

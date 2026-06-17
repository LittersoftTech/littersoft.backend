using System.Collections.Concurrent;
using Pawfront.Application.Events;

namespace Pawfront.Infrastructure.Sql.Events;

internal sealed class InMemoryEventStore : IEventSqlStore
{
    private readonly ConcurrentDictionary<Guid, EventSqlSnapshot> events = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> payoutMethods = new();

    public Task<EventSqlSnapshot> CreateAsync(CreateEventSqlInput input, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EventSqlSnapshot(
            EventId: Guid.NewGuid(),
            ProviderId: input.ProviderId,
            PetParentId: null,
            EventCategory: input.EventCategory,
            IsChildFriendly: input.IsChildFriendly,
            Title: input.Title,
            Description: input.Description,
            BannerImageUrl: input.BannerImageUrl,
            Amenities: input.Amenities.ToArray(),
            EventType: input.EventType,
            StartDate: input.StartDate,
            EndDate: input.EndDate,
            StartTime: input.StartTime,
            EndTime: input.EndTime,
            IsPaid: input.IsPaid,
            Price: input.Price,
            CancellationPolicy: input.CancellationPolicy,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            // Counters aren't tracked in the in-memory dev fallback.
            Counters: new EventCounters(0, 0, 0));

        events[snapshot.EventId] = snapshot;
        return Task.FromResult(snapshot);
    }

    public Task<EventSqlSnapshot> CreateByParentAsync(
        CreateParentEventSqlInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EventSqlSnapshot(
            EventId: Guid.NewGuid(),
            ProviderId: null,
            PetParentId: input.PetParentId,
            EventCategory: input.EventCategory,
            IsChildFriendly: input.IsChildFriendly,
            Title: input.Title,
            Description: input.Description,
            BannerImageUrl: input.BannerImageUrl,
            Amenities: input.Amenities.ToArray(),
            EventType: input.EventType,
            StartDate: input.StartDate,
            EndDate: input.EndDate,
            StartTime: input.StartTime,
            EndTime: input.EndTime,
            IsPaid: input.IsPaid,
            Price: input.Price,
            CancellationPolicy: input.CancellationPolicy,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            // Counters aren't tracked in the in-memory dev fallback.
            Counters: new EventCounters(0, 0, 0));

        events[snapshot.EventId] = snapshot;
        return Task.FromResult(snapshot);
    }

    public Task<EventSqlSnapshot?> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        events.TryGetValue(eventId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<EventSqlSnapshot>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EventSqlSnapshot> list = events.Values
            .Where(e => e.ProviderId == providerId)
            .OrderByDescending(e => e.StartDate)
            .ThenByDescending(e => e.StartTime)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<EventSqlSnapshot>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EventSqlSnapshot> list = events.Values
            .Where(e => e.PetParentId == petParentId)
            .OrderByDescending(e => e.StartDate)
            .ThenByDescending(e => e.StartTime)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<EventSqlSnapshot>> ListAsync(
        EventListFilter filter,
        CancellationToken cancellationToken)
    {
        IEnumerable<EventSqlSnapshot> query = events.Values;

        if (!string.IsNullOrWhiteSpace(filter.EventCategory))
        {
            query = query.Where(e => string.Equals(e.EventCategory, filter.EventCategory, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            query = query.Where(e => string.Equals(e.EventType, filter.EventType, StringComparison.Ordinal));
        }

        if (filter.IsChildFriendly is not null)
        {
            var flag = filter.IsChildFriendly.Value;
            query = query.Where(e => e.IsChildFriendly == flag);
        }

        if (filter.StartDate is not null)
        {
            var from = filter.StartDate.Value;
            query = query.Where(e => e.EndDate >= from);
        }

        if (filter.EndDate is not null)
        {
            var to = filter.EndDate.Value;
            query = query.Where(e => e.StartDate <= to);
        }

        if (filter.Amenities is { Count: > 0 })
        {
            var required = filter.Amenities.ToHashSet(StringComparer.Ordinal);
            query = query.Where(e => required.All(a => e.Amenities.Contains(a, StringComparer.Ordinal)));
        }

        IReadOnlyList<EventSqlSnapshot> list = query
            .OrderByDescending(e => e.StartDate)
            .ThenByDescending(e => e.StartTime)
            .ThenBy(e => e.EventId)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken)
    {
        // Dev fallback only — never used in normal runs. Counters aren't tracked
        // in this in-memory snapshot, so we just return zeros after confirming
        // the event exists.
        if (!events.ContainsKey(eventId))
        {
            throw new EventNotFoundException(eventId);
        }
        return Task.FromResult(new EventCounters(0, 0, 0));
    }

    public Task<IReadOnlyList<string>> SavePayoutMethodsAsync(
        Guid eventId,
        bool acceptsCash,
        bool acceptsDigital,
        CancellationToken cancellationToken)
    {
        if (!events.TryGetValue(eventId, out var snapshot))
        {
            throw new EventNotFoundException(eventId);
        }

        if (!snapshot.IsPaid)
        {
            throw new EventNotPaidException(eventId);
        }

        var saved = new List<string>();
        if (acceptsCash) saved.Add("Cash");
        if (acceptsDigital) saved.Add("Digital");

        payoutMethods[eventId] = saved;
        return Task.FromResult<IReadOnlyList<string>>(saved);
    }
}

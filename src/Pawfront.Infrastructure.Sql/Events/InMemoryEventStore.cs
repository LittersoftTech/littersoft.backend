using System.Collections.Concurrent;
using Pawfront.Application.Events;

namespace Pawfront.Infrastructure.Sql.Events;

internal sealed class InMemoryEventStore : IEventSqlStore
{
    private readonly ConcurrentDictionary<Guid, EventSqlSnapshot> events = new();

    public Task<EventSqlSnapshot> CreateAsync(CreateEventSqlInput input, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EventSqlSnapshot(
            EventId: Guid.NewGuid(),
            ProviderId: input.ProviderId,
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
            CreatedAtUtc: now,
            UpdatedAtUtc: now);

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
}

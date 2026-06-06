namespace Pawfront.Application.Events;

public interface IEventSqlStore
{
    Task<EventSqlSnapshot> CreateAsync(
        CreateEventSqlInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a parent-organised event (PetParentId set, ProviderId NULL).
    /// Throws <see cref="EventPetParentNotFoundException"/> when the parent
    /// row is missing.
    /// </summary>
    Task<EventSqlSnapshot> CreateByParentAsync(
        CreateParentEventSqlInput input,
        CancellationToken cancellationToken);

    Task<EventSqlSnapshot?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSqlSnapshot>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Catalog-wide list with optional filters. See <see cref="EventListFilter"/>
    /// for filter semantics. Skips Cosmos — physical-event details are hydrated
    /// only on the detail endpoint.
    /// </summary>
    Task<IReadOnlyList<EventSqlSnapshot>> ListAsync(
        EventListFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomic single-column increment. <paramref name="counterType"/> must be
    /// one of <c>View</c>, <c>Share</c>, <c>Inquiry</c>. Throws
    /// <see cref="EventNotFoundException"/> if the event id is unknown.
    /// </summary>
    Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken);
}

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

    /// <summary>
    /// Full-replace edit of a provider-organised event. Verifies the event
    /// belongs to the provider (throws <see cref="EventNotFoundException"/> if
    /// not found or not owned), rewrites the editable columns + amenities, and
    /// returns the refreshed detail snapshot (amenities + payment options +
    /// attendee names). Cosmos physical details are reconciled separately by
    /// the caller.
    /// </summary>
    Task<EventSqlSnapshot> UpdateAsync(
        UpdateEventSqlInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Full-replace edit of a parent-organised event. Mirror of
    /// <see cref="UpdateAsync"/> keyed by PetParentId.
    /// </summary>
    Task<EventSqlSnapshot> UpdateByParentAsync(
        UpdateParentEventSqlInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a single event with its full detail (amenities, payment options,
    /// attendee names). The list/create snapshots leave payment options +
    /// attendees null.
    /// </summary>
    Task<EventSqlSnapshot?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSqlSnapshot>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the events organised by a single pet parent (PetParentId set).
    /// Skips Cosmos — physical-event details are hydrated only on the detail
    /// endpoint. List semantics: an empty list when the parent has none.
    /// </summary>
    Task<IReadOnlyList<EventSqlSnapshot>> ListByPetParentAsync(
        Guid petParentId,
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

    /// <summary>
    /// Replaces the event's payout-method set with the given Cash/Digital
    /// flags and returns the saved methods. Throws
    /// <see cref="EventNotFoundException"/> if the event is unknown and
    /// <see cref="EventNotPaidException"/> if the event is free.
    /// </summary>
    Task<IReadOnlyList<string>> SavePayoutMethodsAsync(
        Guid eventId,
        bool acceptsCash,
        bool acceptsDigital,
        CancellationToken cancellationToken);
}

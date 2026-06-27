namespace Pawfront.Application.Events;

public interface IEventService
{
    Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a parent-organised event. Same validation + Cosmos
    /// physical-extension flow as <see cref="CreateAsync"/>, but the row is
    /// keyed by PetParentId instead of ProviderId. Throws
    /// <see cref="EventPetParentNotFoundException"/> when the parent row is missing.
    /// </summary>
    Task<EventResult> CreateByParentAsync(CreateParentEventCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Full-replace edit of a provider-organised event (every field editable).
    /// Validates the same way as create, rewrites the SQL row + amenities,
    /// reconciles the Cosmos physical extension (upsert / delete / re-partition
    /// on category change), and returns the refreshed detail. Throws
    /// <see cref="EventNotFoundException"/> when the event doesn't exist or
    /// isn't owned by the provider.
    /// </summary>
    Task<EventResult> UpdateAsync(UpdateEventCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Full-replace edit of a parent-organised event. Mirror of
    /// <see cref="UpdateAsync"/>; throws <see cref="EventNotFoundException"/>
    /// when the event doesn't exist or isn't owned by the parent.
    /// </summary>
    Task<EventResult> UpdateByParentAsync(UpdateParentEventCommand command, CancellationToken cancellationToken);

    Task<EventResult?> GetAsync(Guid eventId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<EventResult>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the events a pet parent has organised (the parent-host mirror of
    /// <see cref="ListByProviderAsync"/>). Listing skips Cosmos for cost;
    /// the detail endpoint hydrates physical details. Empty list when the
    /// parent has organised none.
    /// </summary>
    Task<IReadOnlyCollection<EventResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Catalog-wide listing with optional filters. Every <see cref="EventListFilter"/>
    /// field is optional; omitting all of them returns every event. Validates enum-
    /// shaped filters (category, type, amenities) and the date-window ordering.
    /// </summary>
    Task<IReadOnlyCollection<EventResult>> ListAsync(
        EventListFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists trending events — ranked by engagement (views + shares +
    /// non-cancelled ticket bookings), most engaging first — capped at
    /// <paramref name="take"/> (the endpoint applies the default; the sproc
    /// clamps to 1..100). Hydrates the Cosmos physical extension like
    /// <see cref="ListAsync"/>. Provider-agnostic: returns events organised by
    /// either providers or pet parents.
    /// </summary>
    Task<IReadOnlyCollection<EventResult>> ListTrendingAsync(
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bumps the requested counter (View | Share | Inquiry) on the event.
    /// Open to any signed-in caller — these are public engagement signals,
    /// not provider-scoped. Throws <see cref="EventNotFoundException"/> if
    /// the event id is unknown.
    /// </summary>
    Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves the event organiser's payout methods (one or more of Cash /
    /// Digital), replacing any previously stored set. Throws
    /// <see cref="EventNotFoundException"/> if the event id is unknown and
    /// <see cref="EventNotPaidException"/> if the event is free (payout
    /// methods only apply to paid events).
    /// </summary>
    Task<EventPayoutMethodsResult> SavePayoutMethodsAsync(
        Guid eventId,
        IReadOnlyCollection<string> payoutMethods,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Events;

public interface IEventService
{
    Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken);

    Task<EventResult?> GetAsync(Guid eventId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<EventResult>> ListByProviderAsync(
        Guid providerId,
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
    /// Bumps the requested counter (View | Share | Inquiry) on the event.
    /// Open to any signed-in caller — these are public engagement signals,
    /// not provider-scoped. Throws <see cref="EventNotFoundException"/> if
    /// the event id is unknown.
    /// </summary>
    Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken);
}

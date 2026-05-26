namespace Pawfront.Application.Events;

public interface IEventService
{
    Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken);

    Task<EventResult?> GetAsync(Guid eventId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<EventResult>> ListByProviderAsync(
        Guid providerId,
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

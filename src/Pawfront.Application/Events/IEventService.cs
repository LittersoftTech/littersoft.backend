namespace Pawfront.Application.Events;

public interface IEventService
{
    Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken);

    Task<EventResult?> GetAsync(Guid eventId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<EventResult>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

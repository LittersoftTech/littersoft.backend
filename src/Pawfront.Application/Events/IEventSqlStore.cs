namespace Pawfront.Application.Events;

public interface IEventSqlStore
{
    Task<EventSqlSnapshot> CreateAsync(
        CreateEventSqlInput input,
        CancellationToken cancellationToken);

    Task<EventSqlSnapshot?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSqlSnapshot>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

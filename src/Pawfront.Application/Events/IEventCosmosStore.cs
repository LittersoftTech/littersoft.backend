namespace Pawfront.Application.Events;

public interface IEventCosmosStore
{
    Task<PhysicalEventResult> UpsertPhysicalAsync(
        Guid eventId,
        string eventCategory,
        PhysicalEventInput details,
        CancellationToken cancellationToken);

    Task<PhysicalEventResult?> GetPhysicalAsync(
        Guid eventId,
        string eventCategory,
        CancellationToken cancellationToken);
}

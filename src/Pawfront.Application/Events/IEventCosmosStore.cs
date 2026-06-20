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

    /// <summary>
    /// Deletes the physical-event extension document for an event. Used when an
    /// edit turns a physical event into an online one, or moves it to a new
    /// category (the category is the Cosmos partition key, so a category change
    /// must delete the old-partition document and re-upsert under the new one).
    /// A missing document is a no-op.
    /// </summary>
    Task DeletePhysicalAsync(
        Guid eventId,
        string eventCategory,
        CancellationToken cancellationToken);
}

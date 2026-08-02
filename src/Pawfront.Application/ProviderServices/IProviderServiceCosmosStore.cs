namespace Pawfront.Application.ProviderServices;

/// <summary>
/// Category-agnostic access to a provider's document in the shared Cosmos
/// <c>ProviderServices</c> container (doc id = ProviderId, partition key =
/// serviceCategory). The five per-category registries own reads and writes of
/// the licence/offering shapes; this exists for operations that don't care what
/// is inside the document — today only the account delete.
/// </summary>
public interface IProviderServiceCosmosStore
{
    /// <summary>
    /// Deletes the provider's document in the given category partition. A
    /// missing document is a no-op.
    /// </summary>
    Task DeleteAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken);
}

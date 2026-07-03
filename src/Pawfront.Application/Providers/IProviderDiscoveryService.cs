namespace Pawfront.Application.Providers;

public interface IProviderDiscoveryService
{
    /// <summary>
    /// Returns a paginated list of provider summary cards matching the
    /// optional category + animals filters. Results are deterministic across
    /// calls (Cosmos default ordering); callers paginating should rely on
    /// <see cref="ProviderDiscoveryFilter.Skip"/> + <see cref="ProviderDiscoveryFilter.Take"/>.
    /// </summary>
    Task<IReadOnlyList<ProviderSummary>> ListAsync(
        ProviderDiscoveryFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a single provider's summary card (business name / image / city /
    /// sub-category) by id within its known service category. Used to enrich a
    /// parent's booking cards with provider details. Returns null when no
    /// offering document exists for that provider in the category.
    /// </summary>
    Task<ProviderSummary?> GetSummaryAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken);
}

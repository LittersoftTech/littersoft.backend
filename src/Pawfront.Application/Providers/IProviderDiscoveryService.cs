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
}

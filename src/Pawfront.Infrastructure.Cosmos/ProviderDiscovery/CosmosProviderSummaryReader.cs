using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Cosmos.ProviderDiscovery;

/// <summary>
/// Serves <see cref="IProviderSummaryReader"/> from the RAW Cosmos discovery
/// reader — the same point read <c>GetSummaryAsync</c> already performs, exposed
/// under a narrow public interface so a host can take it without also taking the
/// active-status and block wrappers (and therefore the SQL infrastructure they
/// need).
///
/// A thin delegation rather than a second query: one implementation reads the
/// offering document, so a provider's business identity cannot differ between the
/// booking detail and their invoice.
/// </summary>
internal sealed class CosmosProviderSummaryReader(
    CosmosProviderDiscoveryService discovery) : IProviderSummaryReader
{
    public Task<ProviderSummary?> GetAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
        => discovery.GetSummaryAsync(providerId, serviceCategory, cancellationToken);
}

using Pawfront.Application.Onboarding;

namespace Pawfront.Infrastructure.Sql.Onboarding;

/// <summary>
/// Minimal stub used when running fully without SQL (dev-only).
/// Reports "provider not found" for every id, which surfaces as a 404 from the API.
/// Real onboarding-status checks require the SQL path.
/// </summary>
internal sealed class InMemoryProviderOnboardingStatusReader : IProviderOnboardingStatusReader
{
    public Task<ProviderOnboardingStatusSnapshot?> ReadAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<ProviderOnboardingStatusSnapshot?>(null);
    }
}

using Pawfront.Application.Policies;
using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderPayoutMethodReader"/>
/// (registered only when there is no SQL connection and Key Vault is disabled).
///
/// FUNCTIONAL rather than a zeros-reporting Null store: the in-memory policy
/// service DOES hold payout methods — the provider saved them through the same
/// endpoint on the same dev machine — so reading them back is both possible and
/// what makes the Payments filter testable without a database. Contrast
/// <c>NullProviderActiveStatusReader</c>, which reports everything active
/// because the in-memory provider store has no such concept at all.
/// </summary>
internal sealed class InMemoryProviderPayoutMethodReader(
    IProviderPolicyService policyService) : IProviderPayoutMethodReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> GetPayoutMethodsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyCollection<string>>(providerIds.Count);
        foreach (var providerId in providerIds.Distinct())
        {
            var policy = await policyService.GetAsync(providerId, cancellationToken);
            if (policy.PayoutMethods is { Count: > 0 })
            {
                result[providerId] = policy.PayoutMethods;
            }
        }
        return result;
    }
}

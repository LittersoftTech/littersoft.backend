using Pawfront.Application.Jobs;

namespace Pawfront.Infrastructure.Sql.Jobs;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderJobStore"/> (registered only
/// when there is no SQL connection and Key Vault is disabled). Reports an empty
/// page.
/// </summary>
/// <remarks>
/// The earnings posture rather than the functional one, and for the same reason
/// <c>NullProviderEarningsStore</c> takes it: this row is an aggregate over both
/// booking tables joined to the payment ledger, the provider-services catalog and
/// the live parent and pet rows. The in-memory stores hold some of that and not
/// the rest, so anything it could return would be half-populated cards — a worse
/// answer than an honest empty list.
/// </remarks>
internal sealed class NullProviderJobStore : IProviderJobStore
{
    public Task<ProviderJobPage> ListAsync(
        ProviderJobQuery query,
        decimal feePercentage,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderJobPage(
            Array.Empty<ProviderJobRow>(), TotalCount: 0, query.Skip, query.Take));
}

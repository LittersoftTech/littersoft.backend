using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderActiveStatusReader"/>
/// (registered only when there is no SQL connection and Key Vault is disabled).
///
/// Reports every candidate as active — the opposite of the SQL reader's
/// fail-closed rule, and deliberately so: the in-memory provider store has no
/// IsActive concept at all, so failing closed would empty discovery and every
/// search on a dev machine running without SQL.
/// </summary>
internal sealed class NullProviderActiveStatusReader : IProviderActiveStatusReader
{
    public Task<IReadOnlySet<Guid>> GetActiveProviderIdsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>(providerIds));
}

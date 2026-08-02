using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderNameReader"/> (registered
/// only when there is no SQL connection and Key Vault is disabled). Returns no
/// names, so freelancer cards fall back to a null display name — the same
/// posture the SQL-backed booking-stats reader takes for that dev config.
/// </summary>
internal sealed class NullProviderNameReader : IProviderNameReader
{
    public Task<IReadOnlyDictionary<Guid, string>> GetProviderDisplayNamesAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

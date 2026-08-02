using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderContactReader"/> (registered
/// only when there is no SQL connection and Key Vault is disabled). Reports no
/// contact, so the provider detail falls back to whatever the Cosmos offering
/// carries — the same posture <see cref="NullProviderNameReader"/> takes.
/// </summary>
internal sealed class NullProviderContactReader : IProviderContactReader
{
    public Task<ProviderContactDetails?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ProviderContactDetails?>(null);
}

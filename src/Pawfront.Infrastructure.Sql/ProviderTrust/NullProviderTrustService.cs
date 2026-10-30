using Pawfront.Application.ProviderTrust;
namespace Pawfront.Infrastructure.Sql.ProviderTrust;
internal sealed class NullProviderTrustService : IProviderTrustService
{
    public Task SyncOfferingDocumentAsync(Guid id,string? url,CancellationToken ct)=>Task.CompletedTask;
    public Task<IReadOnlyDictionary<Guid,string>> GetBadgesAsync(IReadOnlyCollection<Guid> ids,CancellationToken ct)=>Task.FromResult<IReadOnlyDictionary<Guid,string>>(new Dictionary<Guid,string>());
    public Task<ProviderTrustRecord?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<ProviderTrustRecord?>(null);
    public Task<IReadOnlyList<ProviderTrustRecord>> ListPendingAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ProviderTrustRecord>>([]);
    public Task<ProviderTrustRecord> ChangeAsync(Guid id,string action,string? url,Guid? revision,string? reason,string? reference,CancellationToken ct)=>throw new ProviderTrustConflictException("No provider trust storage is configured.");
}

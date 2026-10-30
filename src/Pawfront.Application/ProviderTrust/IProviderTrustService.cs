namespace Pawfront.Application.ProviderTrust;

public sealed record ProviderTrustRecord(Guid ProviderId, Guid RevisionId, string ServiceCategory,
    string EvidenceKind, string Status, string? DocumentUrl, string? RejectionReason,
    string? MedRegReference, DateTimeOffset UpdatedAtUtc);
public sealed record ReviewProviderDocumentRequest(Guid RevisionId, string Decision, string? RejectionReason = null);
public sealed record RecordMedRegRequest(Guid RevisionId, bool Listed, string? Reference);
public interface IProviderTrustService
{
    Task SyncOfferingDocumentAsync(Guid providerId, string? documentUrl, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> GetBadgesAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken ct);
    Task<ProviderTrustRecord?> GetAsync(Guid providerId, CancellationToken ct);
    Task<IReadOnlyList<ProviderTrustRecord>> ListPendingAsync(CancellationToken ct);
    Task<ProviderTrustRecord> ChangeAsync(Guid providerId, string action, string? documentUrl, Guid? revisionId, string? reason, string? reference, CancellationToken ct);
}
public sealed class ProviderTrustConflictException(string message) : Exception(message);
public sealed class ProviderTrustInvalidException(string message) : Exception(message);

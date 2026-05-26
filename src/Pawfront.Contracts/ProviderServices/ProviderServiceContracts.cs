namespace Pawfront.Contracts.ProviderServices;

public sealed record ProviderServiceSummary(
    Guid ServiceId,
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProviderServicesResponse(
    Guid ProviderId,
    IReadOnlyList<ProviderServiceSummary> Services);

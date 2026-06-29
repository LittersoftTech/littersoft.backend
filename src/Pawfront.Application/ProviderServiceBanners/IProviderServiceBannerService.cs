namespace Pawfront.Application.ProviderServiceBanners;

/// <summary>
/// Manages a provider's per-service banner image — one banner per bookable
/// service (<c>Provider.ProviderServices</c> row). Distinct from the Cosmos
/// offering image (the discovery/profile photo): this is a wide banner shown on
/// the service's own screen. Backed by <c>Provider.ProviderServiceBanners</c>.
/// </summary>
public interface IProviderServiceBannerService
{
    /// <summary>
    /// Upserts the banner URL for one of the provider's services. The service
    /// must belong to the provider and be active. Throws
    /// <see cref="ProviderServiceBannerServiceNotFoundException"/> otherwise.
    /// </summary>
    Task<ProviderServiceBannerResult> SaveAsync(
        Guid providerId,
        Guid serviceId,
        string bannerImageUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the banner for a service, or null when none is set.
    /// </summary>
    Task<ProviderServiceBannerResult?> GetAsync(
        Guid serviceId,
        CancellationToken cancellationToken);
}

public sealed record ProviderServiceBannerResult(
    Guid ServiceId,
    Guid ProviderId,
    string BannerImageUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProviderServiceBannerServiceNotFoundException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not valid or active for provider '{providerId}'.");

namespace Pawfront.Application.ProviderBanners;

/// <summary>
/// Manages a provider's single provider-level banner image — the wide picture
/// shown on their card in the parent-facing searches. Stored on
/// <c>Provider.Providers.BannerImageUrl</c>, so it can be set during
/// registration, before the provider has picked a service category.
/// <para>
/// Distinct from <c>IProviderServiceBannerService</c>, which holds one banner
/// per bookable service (ServiceId) and takes precedence on a search card when
/// both are set.
/// </para>
/// </summary>
public interface IProviderBannerImageService
{
    /// <summary>
    /// Overwrites the provider's banner URL. Throws
    /// <see cref="ProviderBannerImageProviderNotFoundException"/> when no
    /// provider row exists.
    /// </summary>
    Task<ProviderBannerImageResult> SaveAsync(
        Guid providerId,
        string bannerImageUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the provider's banner URL, or null when the provider has not set
    /// one (or does not exist).
    /// </summary>
    Task<string?> GetAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Batch-reads banner URLs for a set of providers (used to hydrate search
    /// result cards). Providers without a banner are absent from the map; empty
    /// input yields an empty map.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetByProviderIdsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}

public sealed record ProviderBannerImageResult(
    Guid ProviderId,
    string BannerImageUrl,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProviderBannerImageProviderNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' was not found.");

namespace Pawfront.Contracts.Providers;

/// <summary>
/// Result of setting a provider's provider-level banner image via
/// <c>POST /api/v1/providers/{providerId}/banner-image</c>. The same URL is
/// returned as <c>bannerImageUrl</c> on every provider read (the provider
/// host's profile endpoint and the parent host's public profile).
/// </summary>
public sealed record ProviderBannerImageResponse(
    Guid ProviderId,
    string BannerImageUrl,
    DateTimeOffset UpdatedAtUtc);

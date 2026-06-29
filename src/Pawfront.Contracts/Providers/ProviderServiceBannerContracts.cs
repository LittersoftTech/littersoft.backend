namespace Pawfront.Contracts.Providers;

/// <summary>
/// A provider's banner image for one of their bookable services. Distinct from
/// the offering's discovery/profile image — this is the wide banner shown on the
/// service's own screen. Returned by the per-service banner upload + read.
/// </summary>
public sealed record ProviderServiceBannerResponse(
    Guid ServiceId,
    Guid ProviderId,
    string BannerImageUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

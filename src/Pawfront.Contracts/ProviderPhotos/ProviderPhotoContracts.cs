namespace Pawfront.Contracts.ProviderPhotos;

/// <summary>
/// A single gallery photo owned directly by a provider (not tied to a service).
/// </summary>
public sealed record ProviderPhotoResponse(
    Guid ProviderPhotoId,
    Guid ProviderId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Returned by <c>DELETE /providers/{providerId}/photos/{photoId}</c>. Carries
/// the removed photo's URL so the caller knows which blob was best-effort deleted.
/// </summary>
public sealed record DeleteProviderPhotoResponse(
    Guid ProviderPhotoId,
    Guid ProviderId,
    string PhotoUrl,
    DateTimeOffset DeletedAtUtc);

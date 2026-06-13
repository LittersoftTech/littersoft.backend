using Pawfront.Contracts.ProviderPhotos;

namespace Pawfront.Application.ProviderPhotos;

public interface IProviderPhotoService
{
    /// <summary>
    /// Persists the blob URL of a freshly-uploaded gallery photo to a new row
    /// in <c>Provider.ProviderPhotos</c>. A provider can have many photos, so
    /// each call inserts a new row. The blob upload itself happens at the
    /// endpoint layer. Throws <see cref="ProviderPhotoProviderNotFoundException"/>
    /// when the provider row is missing.
    /// </summary>
    Task<ProviderPhotoResponse> AddAsync(
        Guid providerId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns every gallery photo on file for the given provider, oldest-first.
    /// Returns an empty list when the provider has no photos (or doesn't exist) —
    /// list semantics, no 404.
    /// </summary>
    Task<IReadOnlyList<ProviderPhotoResponse>> ListAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a single gallery photo scoped to the owning provider. Returns the
    /// deleted row (including its URL) so the caller can best-effort delete the
    /// blob. Throws <see cref="ProviderPhotoNotFoundException"/> when no photo
    /// with that id belongs to the provider.
    /// </summary>
    Task<DeleteProviderPhotoResponse> DeleteAsync(
        Guid providerId,
        Guid providerPhotoId,
        CancellationToken cancellationToken);
}

public sealed class ProviderPhotoProviderNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' was not found.");

public sealed class ProviderPhotoNotFoundException(Guid providerPhotoId)
    : Exception($"Provider photo '{providerPhotoId}' was not found.");

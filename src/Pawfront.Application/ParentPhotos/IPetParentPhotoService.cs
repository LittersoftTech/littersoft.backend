using Pawfront.Contracts.ParentPhotos;

namespace Pawfront.Application.ParentPhotos;

public interface IPetParentPhotoService
{
    /// <summary>
    /// Persists the blob URL of a freshly-uploaded gallery photo to a new row
    /// in <c>Parent.PetParentPhotos</c>. A parent can have many photos, so each
    /// call inserts a new row. The blob upload itself happens at the endpoint
    /// layer. Throws
    /// <see cref="Pawfront.Application.ParentOnboarding.PetParentNotFoundException"/>
    /// when the parent row is missing.
    /// </summary>
    Task<PetParentPhotoResponse> AddAsync(
        Guid petParentId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns every gallery photo on file for the given parent, oldest-first.
    /// Returns an empty list when the parent has no photos (or doesn't exist) —
    /// list semantics, no 404.
    /// </summary>
    Task<IReadOnlyList<PetParentPhotoResponse>> ListAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a single gallery photo scoped to the owning parent. Returns the
    /// deleted row (including its URL) so the caller can best-effort delete the
    /// blob. Throws <see cref="PetParentPhotoNotFoundException"/> when no photo
    /// with that id belongs to the parent.
    /// </summary>
    Task<DeletePetParentPhotoResponse> DeleteAsync(
        Guid petParentId,
        Guid petParentPhotoId,
        CancellationToken cancellationToken);
}

public sealed class PetParentPhotoNotFoundException(Guid petParentPhotoId)
    : Exception($"Pet parent photo '{petParentPhotoId}' was not found.");

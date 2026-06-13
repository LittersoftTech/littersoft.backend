namespace Pawfront.Contracts.ParentPhotos;

/// <summary>
/// A single gallery photo owned directly by a pet parent (not tied to a pet).
/// </summary>
public sealed record PetParentPhotoResponse(
    Guid PetParentPhotoId,
    Guid PetParentId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Returned by <c>DELETE /pet-parents/{petParentId}/photos/{photoId}</c>. Carries
/// the removed photo's URL so the caller knows which blob was best-effort deleted.
/// </summary>
public sealed record DeletePetParentPhotoResponse(
    Guid PetParentPhotoId,
    Guid PetParentId,
    string PhotoUrl,
    DateTimeOffset DeletedAtUtc);

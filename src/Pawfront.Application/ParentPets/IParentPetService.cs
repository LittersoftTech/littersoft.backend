using Pawfront.Contracts.ParentPets;

namespace Pawfront.Application.ParentPets;

public interface IParentPetService
{
    /// <summary>
    /// Persists a single pet under the given pet parent. Throws
    /// <see cref="Pawfront.Application.ParentOnboarding.PetParentNotFoundException"/>
    /// when the parent row is missing, and
    /// <see cref="MicrochipIdAlreadyExistsException"/> when the microchip id
    /// collides with an existing pet (microchip IDs are globally unique per
    /// ISO 11784/11785).
    /// </summary>
    Task<PetParentPetResponse> AddPetAsync(
        Guid petParentId,
        AddPetParentPetRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fills in the medical-info fields on an existing pet record
    /// (vaccination, sterilization, medical history, temperament).
    /// Throws <see cref="PetNotFoundException"/> when the pet row is missing.
    /// </summary>
    Task<PetParentPetResponse> UpdateMedicalInfoAsync(
        Guid petId,
        UpdatePetMedicalInfoRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates the basic-info subset of an existing pet (name, breed, type,
    /// gender, DOB, weight, microchip, description). Medical-info columns
    /// are deliberately untouched — use
    /// <see cref="UpdateMedicalInfoAsync"/> for those. Throws
    /// <see cref="PetNotFoundException"/> when the pet row is missing or
    /// <see cref="MicrochipIdAlreadyExistsException"/> when a non-null
    /// microchip id collides with another pet (the UNIQUE filtered index is
    /// global per ISO 11784/11785).
    /// </summary>
    Task<PetParentPetResponse> UpdatePetAsync(
        Guid petId,
        UpdatePetParentPetRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists the blob URL of a freshly-uploaded pet photo to a new row in
    /// <c>Parent.PetPhotos</c>. A pet can have many photos, so each call
    /// inserts a new row. The blob upload itself happens at the endpoint
    /// layer. Throws <see cref="PetNotFoundException"/> when the pet row is
    /// missing.
    /// </summary>
    Task<PetPhotoResponse> AddPhotoAsync(
        Guid petId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns every pet on file for the given parent, each with its full
    /// medical-info snapshot and embedded photo gallery. Returns an empty
    /// list when the parent has no pets (or doesn't exist) — list semantics,
    /// no 404. Photos are ordered oldest-first within each pet so the
    /// gallery renders in upload order.
    /// </summary>
    Task<IReadOnlyList<PetParentPetWithPhotosResponse>> GetPetsAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a single pet's full profile — basic info, medical-info
    /// snapshot, and embedded photo gallery (oldest-first). Returns
    /// <c>null</c> when no pet with the given id exists; the endpoint maps
    /// that to 404 (though the ownership filter normally rejects unknown pets
    /// before the handler runs).
    /// </summary>
    Task<PetParentPetWithPhotosResponse?> GetPetAsync(
        Guid petId,
        CancellationToken cancellationToken);
}

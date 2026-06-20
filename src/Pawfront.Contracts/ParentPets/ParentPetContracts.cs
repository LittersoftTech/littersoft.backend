namespace Pawfront.Contracts.ParentPets;

public sealed record AddPetParentPetRequest(
    string PetType,
    string PetName,
    string Breed,
    string Gender,
    DateOnly DateOfBirth,
    decimal Weight,
    string? MicrochipId,
    string? Description);

public sealed record PetParentPetResponse(
    Guid PetId,
    Guid PetParentId,
    string PetType,
    string PetName,
    string Breed,
    string Gender,
    DateOnly DateOfBirth,
    decimal Weight,
    string? MicrochipId,
    string? Description,
    // Medical-info fields below — all nullable because pets are first
    // created via POST /pets (no medical info) and later patched via
    // PATCH /pets/{petId}/medical-info.
    string? VaccinationStatus,
    string? SterilizationStatus,
    string? MedicalHistory,
    string? Temperament,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    // The pet's single primary/profile photo (distinct from the gallery). Null
    // until set via POST /pets/{petId}/profile-image.
    string? ProfilePhotoUrl);

public sealed record UpdatePetMedicalInfoRequest(
    string VaccinationStatus,
    string SterilizationStatus,
    string? MedicalHistory,
    // Optional — a pet can be added without a known temperament. When omitted
    // or empty it's stored as null; when present it must be a valid value.
    string? Temperament);

/// <summary>
/// Edits the basic-info subset of a pet (everything captured at AddPet time
/// — name, breed, type, gender, DOB, weight, microchip, description). The
/// medical-info subset is intentionally NOT in this shape; use
/// <see cref="UpdatePetMedicalInfoRequest"/> for that. Distinct from
/// <see cref="AddPetParentPetRequest"/> at the type level so the contract
/// stays explicit even though the fields happen to match today.
/// </summary>
public sealed record UpdatePetParentPetRequest(
    string PetType,
    string PetName,
    string Breed,
    string Gender,
    DateOnly DateOfBirth,
    decimal Weight,
    string? MicrochipId,
    string? Description);

public sealed record PetPhotoResponse(
    Guid PetPhotoId,
    Guid PetId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Returned by <c>DELETE /pets/{petId}</c>. The pet row and its photo rows
/// (cascade) are gone; any bookings that referenced the pet are detached
/// (their <c>petId</c> set null, snapshots preserved). Photo blobs are left
/// for a future cleanup sweep.
/// </summary>
public sealed record DeletePetResponse(
    Guid PetId,
    Guid PetParentId,
    DateTimeOffset DeletedAtUtc);

/// <summary>
/// Returned by <c>DELETE /pets/{petId}/photos/{photoId}</c>. Carries the
/// removed photo's URL so the caller knows which blob was best-effort deleted.
/// </summary>
public sealed record DeletePetPhotoResponse(
    Guid PetPhotoId,
    Guid PetId,
    string PhotoUrl,
    DateTimeOffset DeletedAtUtc);

/// <summary>
/// Full pet snapshot returned by <c>GET /api/v1/pet-parents/{petParentId}/pets</c>.
/// Same fields as <see cref="PetParentPetResponse"/> plus the embedded photo
/// gallery. Kept as a separate type so AddPet and PATCH medical-info responses
/// stay unchanged (those flows don't need to re-fetch photos).
/// </summary>
public sealed record PetParentPetWithPhotosResponse(
    Guid PetId,
    Guid PetParentId,
    string PetType,
    string PetName,
    string Breed,
    string Gender,
    DateOnly DateOfBirth,
    decimal Weight,
    string? MicrochipId,
    string? Description,
    string? VaccinationStatus,
    string? SterilizationStatus,
    string? MedicalHistory,
    string? Temperament,
    IReadOnlyList<PetPhotoResponse> Photos,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    // The pet's single primary/profile photo (distinct from the gallery above).
    string? ProfilePhotoUrl);

/// <summary>
/// Returned by <c>POST /pets/{petId}/profile-image</c>. Slim confirmation —
/// the pet's new profile-photo URL plus when it changed. Mirror of the parent
/// host's profile-photo response shape.
/// </summary>
public sealed record UpdatePetProfilePhotoResponse(
    Guid PetId,
    string ProfilePhotoUrl,
    DateTimeOffset UpdatedAtUtc);

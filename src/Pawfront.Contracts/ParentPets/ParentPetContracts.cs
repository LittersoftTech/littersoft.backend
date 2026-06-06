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
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdatePetMedicalInfoRequest(
    string VaccinationStatus,
    string SterilizationStatus,
    string? MedicalHistory,
    string Temperament);

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
    DateTimeOffset UpdatedAtUtc);

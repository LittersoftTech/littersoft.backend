namespace Pawfront.Contracts.ParentPets;

/// <summary>Completion is derived from persisted fields, never supplied by the client.</summary>
public static class PetProfileCompletion
{
    public static bool IsComplete(string? name, string? species, string? breed,
        string? gender, DateOnly dateOfBirth, string? vaccinationStatus,
        string? sterilizationStatus, string? profilePhotoUrl) =>
        HasDetails(name, species, breed, gender, dateOfBirth)
        && HasMedicalInfo(vaccinationStatus, sterilizationStatus)
        && !string.IsNullOrWhiteSpace(profilePhotoUrl);

    public static bool HasDetails(string? name, string? species, string? breed,
        string? gender, DateOnly dateOfBirth) =>
        !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(species)
        && !string.IsNullOrWhiteSpace(breed) && !string.IsNullOrWhiteSpace(gender)
        && dateOfBirth != default;

    // Both required fields are saved together by the medical-info submission.
    // Optional temperament, history and prescription do not gate completion.
    public static bool HasMedicalInfo(string? vaccinationStatus, string? sterilizationStatus) =>
        !string.IsNullOrWhiteSpace(vaccinationStatus)
        && !string.IsNullOrWhiteSpace(sterilizationStatus);
}

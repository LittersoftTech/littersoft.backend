namespace Pawfront.Contracts.Services.PetTrainer;

public sealed record RegisterTrainingSchoolRequest(
    string TrainingSchoolName,
    string Address,
    string Zip,
    string City,
    string? TelephoneCountryCode,
    string? TelephoneNumber,
    string Email,
    string? Website,
    string? Description,
    string? SchoolImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterFreelanceTrainerRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string? AboutYou,
    string? ProfileImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record SaveTrainingSchoolOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    TrainingSessionRequest Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string>? PreviousExperience,
    // Optional. Carries a C# default so it stays out of the OpenAPI `required`
    // list; blank/omitted reads back as "" on this host and as null on the two
    // parent-facing surfaces that show it.
    string? PrivateTrainingDescription = null);

public sealed record SaveFreelanceTrainerOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    TrainingSessionRequest Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string>? PreviousExperience,
    // Optional. Carries a C# default so it stays out of the OpenAPI `required`
    // list; blank/omitted reads back as "" on this host and as null on the two
    // parent-facing surfaces that show it.
    string? PrivateTrainingDescription = null);

public sealed record TrainingSessionRequest(
    decimal SessionDurationHours,
    decimal PricePerSession);

public sealed record PetTrainerServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    TrainingSchoolResponse? TrainingSchool,
    FreelanceTrainerResponse? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TrainingSchoolResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetTrainerLicenseResponse? License,
    PetTrainerOfferingResponse? Offering);

public sealed record FreelanceTrainerResponse(
    string AboutYou,
    string? ImageUrl,
    PetTrainerLicenseResponse? License,
    PetTrainerOfferingResponse? Offering);

public sealed record PetTrainerLicenseResponse(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetTrainerOfferingResponse(
    TrainingSessionResponse? Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string> PreviousExperience,
    string PrivateTrainingDescription);

public sealed record TrainingSessionResponse(
    decimal SessionDurationHours,
    decimal PricePerSession);

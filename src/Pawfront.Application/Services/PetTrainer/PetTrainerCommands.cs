namespace Pawfront.Application.Services.PetTrainer;

public sealed record RegisterTrainingSchoolCommand(
    Guid ProviderId,
    string TrainingSchoolName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? SchoolImageUrl);

public sealed record RegisterFreelanceTrainerCommand(
    Guid ProviderId,
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl);

public sealed record SaveTrainingSchoolOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    TrainingSessionInput Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string>? PreviousExperience,
    string PrivateTrainingDescription);

public sealed record SaveFreelanceTrainerOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    TrainingSessionInput Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string>? PreviousExperience,
    string PrivateTrainingDescription);

public sealed record TrainingSessionInput(
    decimal SessionDurationHours,
    decimal PricePerSession);

public sealed record PetTrainerServiceResult(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    TrainingSchoolResult? TrainingSchool,
    FreelanceTrainerResult? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TrainingSchoolResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetTrainerLicenseResult? License,
    PetTrainerOfferingResult? Offering);

public sealed record FreelanceTrainerResult(
    string AboutYou,
    string? ImageUrl,
    PetTrainerLicenseResult? License,
    PetTrainerOfferingResult? Offering);

public sealed record PetTrainerLicenseResult(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetTrainerOfferingResult(
    TrainingSessionResult? Session,
    IReadOnlyCollection<string> PetsTrained,
    IReadOnlyCollection<string> AgeGroups,
    IReadOnlyCollection<string> Temperaments,
    int MaxConcurrentSessions,
    IReadOnlyCollection<string> ServiceLocations,
    IReadOnlyCollection<string> TrainingApproaches,
    IReadOnlyCollection<string> PreviousExperience,
    string PrivateTrainingDescription);

public sealed record TrainingSessionResult(
    decimal SessionDurationHours,
    decimal PricePerSession);

public sealed class TrainingSchoolNotRegisteredException(Guid providerId)
    : Exception($"Training School registration was not found for provider '{providerId}'. Complete the basic Training School registration first.");

public sealed class FreelanceTrainerNotRegisteredException(Guid providerId)
    : Exception($"Freelance Trainer registration was not found for provider '{providerId}'. Complete the basic Freelance Trainer registration first.");

using Pawfront.Application.Onboarding;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Domain model of the pet-parent onboarding progress, computed by
/// <see cref="IPetParentOnboardingStatusService"/> from a single round-trip
/// snapshot. Status strings reuse the provider-side
/// <see cref="OnboardingStageStatuses"/> constants (Complete / Remaining).
/// </summary>
public sealed record PetParentOnboardingStatus(
    Guid PetParentId,
    PetParentOnboardingStage BasicInfo,
    PetParentOnboardingStage ProfilePhoto,
    PetParentPetsStage Pets,
    PetParentPetMedicalInfoStage PetMedicalInfo,
    PetParentIdentityStage Identity,
    PetParentVerificationStatus Verification,
    bool IsFullyOnboarded);

/// <summary>
/// Identity-verification stage on the onboarding status. IdentityType
/// is null while the parent hasn't uploaded; populated with the declared
/// type once the upload row exists.
/// </summary>
public sealed record PetParentIdentityStage(string Status, string? IdentityType);

public sealed record PetParentOnboardingStage(string Status);

public sealed record PetParentPetsStage(
    string Status,
    int PetCount);

public sealed record PetParentPetMedicalInfoStage(
    string Status,
    IReadOnlyCollection<PetMedicalInfoCompletion> Pets);

public sealed record PetMedicalInfoCompletion(
    Guid PetId,
    string PetName,
    bool IsMedicalInfoComplete);

public sealed record PetParentVerificationStatus(
    bool IsEmailVerified,
    bool IsMobileVerified);

/// <summary>
/// Raw aggregate read from the SQL sproc; the orchestrator turns this into
/// the public <see cref="PetParentOnboardingStatus"/> by computing stage
/// statuses and the <c>IsFullyOnboarded</c> roll-up.
/// </summary>
public sealed record PetParentOnboardingStatusSnapshot(
    Guid PetParentId,
    string? ProfilePhotoUrl,
    bool IsEmailVerified,
    bool IsMobileVerified,
    IReadOnlyCollection<PetMedicalInfoCompletion> Pets,
    string? IdentityType);

public sealed class PetParentOnboardingStatusNotFoundException(Guid petParentId)
    : Exception($"Pet parent '{petParentId}' was not found.");

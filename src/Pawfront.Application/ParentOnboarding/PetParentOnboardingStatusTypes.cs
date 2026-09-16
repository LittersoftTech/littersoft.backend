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

/// <summary>
/// The "has the parent added their pets" stage, plus a pointer straight at the
/// first pet that still needs finishing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IncompletePetId"/> exists so a "Complete Profile" button can open
/// the exact pet instead of the app guessing which of several is unfinished. It
/// names the FIRST pet (in the order they were added) missing a section that
/// actually gates onboarding — today that is medical info and nothing else. It is
/// null exactly when <see cref="PetParentOnboardingStatus.PetMedicalInfo"/> reads
/// Complete, so a parent with nothing left to do is never sent to a pet screen.
/// </para>
/// <para>
/// <see cref="MissingSections"/> is that pet's missing sections, and can include
/// non-gating ones (a pet with no profile photo but complete medical info is not
/// what <see cref="IncompletePetId"/> points at, but if it IS the incomplete pet
/// its missing photo is listed too). The per-pet list on the medical-info stage
/// carries the same sections for every pet.
/// </para>
/// </remarks>
public sealed record PetParentPetsStage(
    string Status,
    int PetCount,
    Guid? IncompletePetId = null,
    string? IncompletePetName = null,
    IReadOnlyCollection<string>? MissingSections = null);

/// <summary>
/// The sections of a pet's profile that the onboarding status can report as
/// missing.
/// </summary>
/// <remarks>
/// There is deliberately no <c>BasicInfo</c> value: every basic-info column on
/// <c>Parent.Pets</c> is NOT NULL, so a pet that exists always has it, and a
/// section that can never be missing would be noise on every response.
/// </remarks>
public static class PetProfileSections
{
    /// <summary>
    /// Vaccination + sterilization status. The ONLY section that gates
    /// <c>isFullyOnboarded</c>; free-text medical history and temperament are
    /// optional and not checked.
    /// </summary>
    public const string MedicalInfo = "MedicalInfo";

    /// <summary>
    /// The pet's single primary photo. Reported so the app can prompt for it, but
    /// NOT gating — exactly as the parent's own profile photo is not.
    /// </summary>
    public const string ProfilePhoto = "ProfilePhoto";
}

public sealed record PetParentPetMedicalInfoStage(
    string Status,
    IReadOnlyCollection<PetMedicalInfoCompletion> Pets);

/// <summary>
/// One pet's completion state. <see cref="MissingSections"/> names what is
/// unfinished, drawn from <see cref="PetProfileSections"/>, and is empty when
/// nothing is.
/// </summary>
public sealed record PetMedicalInfoCompletion(
    Guid PetId,
    string PetName,
    bool IsMedicalInfoComplete,
    bool HasProfilePhoto = false,
    IReadOnlyCollection<string>? MissingSections = null);

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

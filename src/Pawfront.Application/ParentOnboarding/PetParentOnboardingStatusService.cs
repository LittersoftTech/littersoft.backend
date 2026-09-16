using Pawfront.Application.Onboarding;

namespace Pawfront.Application.ParentOnboarding;

internal sealed class PetParentOnboardingStatusService(
    IPetParentOnboardingStatusReader reader) : IPetParentOnboardingStatusService
{
    public async Task<PetParentOnboardingStatus> GetAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        var snapshot = await reader.ReadAsync(petParentId, cancellationToken);
        if (snapshot is null)
        {
            throw new PetParentOnboardingStatusNotFoundException(petParentId);
        }

        // Stage 1 — BasicInfo: always Complete once the parent profile row
        // exists (which is the precondition for this endpoint resolving).
        var basicInfo = new PetParentOnboardingStage(OnboardingStageStatuses.Complete);

        // Stage 2 — ProfilePhoto: nullable URL is the source of truth.
        var profilePhoto = new PetParentOnboardingStage(
            string.IsNullOrWhiteSpace(snapshot.ProfilePhotoUrl)
                ? OnboardingStageStatuses.Remaining
                : OnboardingStageStatuses.Complete);

        // Per-pet section lists, computed once and shared by both pet stages so
        // the pointer below and the medical-info list can never name different
        // sections for the same pet.
        var petsWithSections = snapshot.Pets
            .Select(p => p with { MissingSections = MissingSectionsFor(p) })
            .ToArray();

        // The first pet still missing a GATING section, so a "Complete Profile"
        // button can open that exact pet instead of the app guessing which of
        // several is unfinished. Medical info is the only gating section today, so
        // this is null exactly when stage 4 below reads Complete — a parent with
        // nothing left to do is never pointed at a pet screen. Order is the order
        // the pets were added (the sproc's ORDER BY CreatedAtUtc).
        var firstIncomplete = petsWithSections.FirstOrDefault(p => !p.IsMedicalInfoComplete);

        // Stage 3 — Pets: at least one pet on file.
        var pets = new PetParentPetsStage(
            snapshot.Pets.Count > 0
                ? OnboardingStageStatuses.Complete
                : OnboardingStageStatuses.Remaining,
            snapshot.Pets.Count,
            firstIncomplete?.PetId,
            firstIncomplete?.PetName,
            firstIncomplete?.MissingSections);

        // Stage 4 — PetMedicalInfo: every pet has VaccinationStatus,
        // SterilizationStatus, and Temperament set (free-text MedicalHistory
        // is optional and not part of the completion check — the sproc CASE
        // applies the same rule).
        var allPetsMedicalComplete = petsWithSections.Length > 0
            && petsWithSections.All(p => p.IsMedicalInfoComplete);
        var petMedicalInfo = new PetParentPetMedicalInfoStage(
            allPetsMedicalComplete
                ? OnboardingStageStatuses.Complete
                : OnboardingStageStatuses.Remaining,
            petsWithSections);

        // Stage 5 — Identity. Snapshot.IdentityType is null until the
        // parent uploads via POST /pet-parents/{id}/identity. The
        // IdentityType is surfaced so the mobile UI can render "Verified
        // with Passport" etc.
        var identity = new PetParentIdentityStage(
            string.IsNullOrWhiteSpace(snapshot.IdentityType)
                ? OnboardingStageStatuses.Remaining
                : OnboardingStageStatuses.Complete,
            snapshot.IdentityType);

        // Stage 6 — Verification. IsEmailVerified comes from Firebase
        // (ParentAuthIdentities); IsMobileVerified flips true once the
        // mobile-verification OTP flow succeeds (Parent.PetParents
        // .MobileVerifiedAtUtc is set by Parent.VerifyMobileVerificationOtp).
        var verification = new PetParentVerificationStatus(
            snapshot.IsEmailVerified,
            snapshot.IsMobileVerified);

        // Fully-onboarded gate: BasicInfo + Pets + PetMedicalInfo +
        // Identity + email verified + mobile verified. ProfilePhoto is
        // informational and intentionally NOT part of the gate.
        var isFullyOnboarded =
            basicInfo.Status == OnboardingStageStatuses.Complete
            && pets.Status == OnboardingStageStatuses.Complete
            && petMedicalInfo.Status == OnboardingStageStatuses.Complete
            && identity.Status == OnboardingStageStatuses.Complete
            && verification.IsEmailVerified
            && verification.IsMobileVerified;

        return new PetParentOnboardingStatus(
            petParentId,
            basicInfo,
            profilePhoto,
            pets,
            petMedicalInfo,
            identity,
            verification,
            isFullyOnboarded);
    }

    /// <summary>
    /// What is still unfinished on one pet, named so the app can deep-link to the
    /// right screen rather than reopening the whole pet form.
    /// </summary>
    /// <remarks>
    /// The list mixes gating and non-gating sections on purpose: the app decides
    /// how hard to push, and hiding the photo prompt here would mean the only way
    /// to discover it was to fetch the pet. Only <c>MedicalInfo</c> affects
    /// <c>isFullyOnboarded</c>. No basic-info section exists — those columns are
    /// NOT NULL, so a pet that exists always has them.
    /// </remarks>
    private static IReadOnlyCollection<string> MissingSectionsFor(PetMedicalInfoCompletion pet)
    {
        var missing = new List<string>(2);
        if (!pet.IsMedicalInfoComplete)
        {
            missing.Add(PetProfileSections.MedicalInfo);
        }
        if (!pet.HasProfilePhoto)
        {
            missing.Add(PetProfileSections.ProfilePhoto);
        }
        return missing;
    }
}

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

        // Stage 3 — Pets: at least one pet on file.
        var pets = new PetParentPetsStage(
            snapshot.Pets.Count > 0
                ? OnboardingStageStatuses.Complete
                : OnboardingStageStatuses.Remaining,
            snapshot.Pets.Count);

        // Stage 4 — PetMedicalInfo: every pet has VaccinationStatus,
        // SterilizationStatus, and Temperament set (free-text MedicalHistory
        // is optional and not part of the completion check — the sproc CASE
        // applies the same rule).
        var allPetsMedicalComplete = snapshot.Pets.Count > 0
            && snapshot.Pets.All(p => p.IsMedicalInfoComplete);
        var petMedicalInfo = new PetParentPetMedicalInfoStage(
            allPetsMedicalComplete
                ? OnboardingStageStatuses.Complete
                : OnboardingStageStatuses.Remaining,
            snapshot.Pets);

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
}

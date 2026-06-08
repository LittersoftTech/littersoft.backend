using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Application.ParentOnboarding;

public interface IParentOnboardingService
{
    Task<ParentFirebaseAuthResponse> SaveFirebaseAuthAsync(
        SaveParentFirebaseAuthCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates the pet-parent profile row, links it back to the auth identity
    /// (sets <c>PetParentId</c> + flips <c>SignUpStatus</c> to
    /// <c>ParentProfileCompleted</c>), and back-fills <c>PetParentId</c> on any
    /// existing device tokens. Idempotent: a second call after the profile is
    /// linked just returns the existing row without re-inserting.
    /// </summary>
    /// <summary>
    /// Creates the parent profile row for the caller. The owning auth
    /// identity is looked up from <paramref name="firebaseUserId"/> (the
    /// caller's JWT sub/user_id), never from the request body. Throws
    /// <see cref="ParentAuthIdentityNotFoundException"/> when no auth
    /// identity exists for the Firebase user yet (caller must complete
    /// <c>firebase-auth</c> first).
    /// </summary>
    Task<PetParentProfileResponse> CompletePetParentProfileAsync(
        string firebaseUserId,
        CompletePetParentProfileRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists the blob URL of the parent's freshly-uploaded profile photo to
    /// <c>Parent.PetParents.ProfilePhotoUrl</c>. The blob upload happens at the
    /// endpoint layer; this method only writes the URL. Throws
    /// <see cref="PetParentNotFoundException"/> when the row is missing.
    /// </summary>
    Task<UpdatePetParentProfilePhotoResponse> UpdateProfilePhotoAsync(
        Guid petParentId,
        string profilePhotoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates a fresh 6-digit OTP, persists the SHA-256 hash with a
    /// 10-minute expiry on <c>Parent.ParentMobileOtps</c>, and dispatches the
    /// raw code to the parent's mobile via <see cref="IPetParentMobileOtpSender"/>.
    /// Throws <see cref="PetParentNotFoundException"/> when the parent row
    /// is missing.
    /// </summary>
    Task<SendPetParentMobileOtpResponse> SendMobileOtpAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates the OTP entry. Always returns successfully (no exception
    /// for wrong/expired/already-validated cases) so the client can branch
    /// on <c>validationStatus</c> + <c>isValidated</c>. Throws
    /// <see cref="ParentMobileOtpNotFoundException"/> only when the OTP id
    /// doesn't exist (or doesn't belong to the parent).
    /// </summary>
    Task<VerifyPetParentMobileOtpResponse> VerifyMobileOtpAsync(
        Guid petParentId,
        Guid parentMobileOtpId,
        VerifyPetParentMobileOtpRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces the parent's identity row (one identity per
    /// parent). Throws <see cref="PetParentNotFoundException"/> when the
    /// parent row is missing, or
    /// <see cref="UnsupportedPetParentIdentityTypeException"/> when the
    /// supplied <paramref name="identityType"/> isn't in the supported set.
    /// </summary>
    Task<UpsertPetParentIdentityResponse> UpsertIdentityAsync(
        Guid petParentId,
        string identityType,
        string identityPhotoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a Firebase user id to the associated pet-parent auth identity
    /// and (if one exists) the pet-parent profile. Used by the mobile app after
    /// a reinstall to recover its PetParentId from the current Firebase session.
    /// Throws <see cref="ParentAuthIdentityNotFoundException"/> when no auth
    /// identity exists for the Firebase user id.
    /// </summary>
    Task<ResolvePetParentByFirebaseUidResponse> ResolvePetParentByFirebaseUidAsync(
        string firebaseUserId,
        CancellationToken cancellationToken);
}

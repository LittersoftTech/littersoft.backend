namespace Pawfront.Contracts.ParentOnboarding;

public sealed record SaveParentFirebaseAuthRequest(
    string? FcmToken,
    string? DeviceId,
    string? DevicePlatform);

public sealed record ParentFirebaseAuthResponse(
    Guid ParentAuthIdentityId,
    Guid? PetParentId,
    string FirebaseUserId,
    string AuthProvider,
    string? FirebaseProviderId,
    string Email,
    bool IsEmailVerified,
    string? DisplayName,
    string? FirebasePhoneNumber,
    string? PhotoUrl,
    string? FirebaseTenantId,
    string SignUpStatus,
    DateTimeOffset LastSignedInAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Body for <c>POST /api/v1/parent-onboarding/profile</c>. The owning
/// auth identity is resolved server-side from the caller's Firebase JWT
/// (sub/user_id claim) — never from the body — so a malicious caller can't
/// complete a different parent's profile by guessing the auth identity id.
/// </summary>
public sealed record CompletePetParentProfileRequest(
    string FirstName,
    string LastName,
    string Gender,
    string MobileCountryCode,
    string MobileNumber,
    DateOnly DateOfBirth,
    string AddressLine,
    decimal Latitude,
    decimal Longitude,
    string ZipCode,
    string City,
    string Description);

public sealed record PetParentProfileResponse(
    Guid PetParentId,
    Guid ParentAuthIdentityId,
    string FirstName,
    string LastName,
    string Gender,
    string MobileCountryCode,
    string MobileNumber,
    DateOnly DateOfBirth,
    string AddressLine,
    decimal Latitude,
    decimal Longitude,
    string ZipCode,
    string City,
    string Description,
    string? ProfilePhotoUrl,
    DateTimeOffset? MobileVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdatePetParentProfilePhotoResponse(
    Guid PetParentId,
    string ProfilePhotoUrl,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Body for <c>PATCH /pet-parents/{petParentId}/profile</c>. The editable
/// subset only — mobile number is intentionally excluded (a change must go
/// back through OTP verification), as are latitude/longitude (no
/// coordinates accompany an address edit today) and the profile photo
/// (own endpoint).
/// </summary>
public sealed record UpdatePetParentProfileRequest(
    string FirstName,
    string LastName,
    string Gender,
    DateOnly DateOfBirth,
    string AddressLine,
    string ZipCode,
    string City,
    string Description);

/// <summary>
/// Full profile read-back returned by <c>GET /pet-parents/{petParentId}/profile</c>
/// and by the profile edit. Email + IsEmailVerified come from the linked
/// auth identity; everything else from <c>Parent.PetParents</c>.
/// </summary>
public sealed record PetParentProfileDetailsResponse(
    Guid PetParentId,
    string FirstName,
    string LastName,
    string Gender,
    string Email,
    bool IsEmailVerified,
    string MobileCountryCode,
    string MobileNumber,
    DateOnly DateOfBirth,
    string AddressLine,
    decimal Latitude,
    decimal Longitude,
    string ZipCode,
    string City,
    string Description,
    string? ProfilePhotoUrl,
    DateTimeOffset? MobileVerifiedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Response for <c>GET /api/v1/parent-onboarding/me</c>. Resolves the
/// caller's Firebase uid → the persisted pet-parent auth identity and (if
/// one exists) the linked profile row. Used by the mobile app after a
/// reinstall to recover its <c>PetParentId</c> from the current Firebase
/// session. <c>PetParentId</c>, <c>HasProfile</c>, and
/// <c>MobileVerifiedAtUtc</c> are populated only once the parent has
/// completed <c>POST /parent-onboarding/profile</c>.
/// </summary>
public sealed record ResolvePetParentByFirebaseUidResponse(
    Guid ParentAuthIdentityId,
    Guid? PetParentId,
    string FirebaseUserId,
    string Email,
    bool IsEmailVerified,
    string? DisplayName,
    string SignUpStatus,
    bool HasProfile,
    DateTimeOffset? MobileVerifiedAtUtc);

public sealed record PetParentOnboardingStatusResponse(
    Guid PetParentId,
    OnboardingStageResponse BasicInfo,
    OnboardingStageResponse ProfilePhoto,
    PetsStageResponse Pets,
    PetMedicalInfoStageResponse PetMedicalInfo,
    IdentityStageResponse Identity,
    PetParentVerificationStatusResponse Verification,
    bool IsFullyOnboarded);

/// <summary>
/// Identity-verification stage on the onboarding status payload. The
/// <see cref="IdentityType"/> is null while <see cref="Status"/> is
/// <c>Remaining</c>; populated with the declared type (Passport, etc.)
/// once the parent uploads.
/// </summary>
public sealed record IdentityStageResponse(string Status, string? IdentityType);

public sealed record OnboardingStageResponse(string Status);

public sealed record PetsStageResponse(string Status, int PetCount);

public sealed record PetMedicalInfoStageResponse(
    string Status,
    IReadOnlyCollection<PetMedicalInfoCompletionResponse> Pets);

public sealed record PetMedicalInfoCompletionResponse(
    Guid PetId,
    string PetName,
    bool IsMedicalInfoComplete);

public sealed record PetParentVerificationStatusResponse(
    bool IsEmailVerified,
    bool IsMobileVerified);

public sealed record SendPetParentMobileOtpResponse(
    Guid ParentMobileOtpId,
    Guid PetParentId,
    string MobileCountryCode,
    string MobileNumber,
    DateTimeOffset DateSentUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record VerifyPetParentMobileOtpRequest(
    string OtpCode);

public sealed record VerifyPetParentMobileOtpResponse(
    Guid ParentMobileOtpId,
    Guid PetParentId,
    bool IsValidated,
    string ValidationStatus,
    DateTimeOffset DateSentUtc,
    DateTimeOffset? DateValidatedUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Saved identity record returned by
/// <c>POST /api/v1/pet-parents/{petParentId}/identity</c>. One row per
/// parent — re-uploading replaces the previous entry.
/// </summary>
public sealed record UpsertPetParentIdentityResponse(
    Guid ParentIdentityId,
    Guid PetParentId,
    string IdentityType,
    string IdentityPhotoUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Result of <c>DELETE /api/v1/pet-parents/{petParentId}/identity</c>. The
/// onboarding-status identity stage reverts to Remaining (and
/// isFullyOnboarded to false) until a new document is uploaded.
/// </summary>
public sealed record DeletePetParentIdentityResponse(
    Guid ParentIdentityId,
    Guid PetParentId,
    string IdentityType,
    // URL the deleted row pointed at. The blob itself is also deleted
    // (best-effort) — kept on the wire mainly so the client can clear any
    // cached copy.
    string IdentityPhotoUrl,
    DateTimeOffset DeletedAtUtc);

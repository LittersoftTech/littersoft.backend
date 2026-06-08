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

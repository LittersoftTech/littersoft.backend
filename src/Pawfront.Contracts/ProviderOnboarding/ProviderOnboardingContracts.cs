namespace Pawfront.Contracts.ProviderOnboarding;

public sealed record SaveProviderFirebaseAuthRequest(
    string? FcmToken,
    string? DeviceId,
    string? DevicePlatform);

public sealed record ProviderFirebaseAuthResponse(
    Guid ProviderAuthIdentityId,
    Guid? ProviderId,
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

public sealed record CompleteProviderProfileRequest(
    Guid ProviderAuthIdentityId,
    string FirstName,
    string LastName,
    string Gender,
    string MobileCountryCode,
    string MobileNumber,
    DateOnly DateOfBirth);

public sealed record ProviderProfileResponse(
    Guid ProviderId,
    Guid ProviderAuthIdentityId,
    string FirstName,
    string LastName,
    string Gender,
    string MobileCountryCode,
    string MobileNumber,
    DateOnly DateOfBirth,
    DateTimeOffset? MobileVerifiedAtUtc,
    string OnboardingStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SendProviderMobileOtpResponse(
    Guid ProviderMobileOtpId,
    Guid ProviderId,
    string MobileCountryCode,
    string MobileNumber,
    DateTimeOffset DateSentUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record VerifyProviderMobileOtpRequest(
    string OtpCode);

public sealed record VerifyProviderMobileOtpResponse(
    Guid ProviderMobileOtpId,
    Guid ProviderId,
    bool IsValidated,
    string ValidationStatus,
    DateTimeOffset DateSentUtc,
    DateTimeOffset? DateValidatedUtc,
    DateTimeOffset ExpiresAtUtc);

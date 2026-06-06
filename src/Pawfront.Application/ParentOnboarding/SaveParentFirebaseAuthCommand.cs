namespace Pawfront.Application.ParentOnboarding;

public sealed record SaveParentFirebaseAuthCommand(
    string FirebaseUserId,
    string AuthProvider,
    string? FirebaseProviderId,
    string Email,
    bool IsEmailVerified,
    string? DisplayName,
    string? FirebasePhoneNumber,
    string? PhotoUrl,
    string? FirebaseTenantId,
    string? FcmToken,
    string? DeviceId,
    string? DevicePlatform);

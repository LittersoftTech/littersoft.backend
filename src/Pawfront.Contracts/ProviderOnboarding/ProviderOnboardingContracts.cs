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

/// <summary>
/// Edits the provider's personal details. Mobile number + country code are
/// deliberately absent — a change there must go back through OTP verification.
/// </summary>
public sealed record UpdateProviderProfileRequest(
    string FirstName,
    string LastName,
    string Gender,
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
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    // Wide banner shown on the provider's card in the parent-facing searches.
    // Set via POST /providers/{providerId}/banner-image; null until uploaded.
    string? BannerImageUrl);

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

public sealed record ResolveProviderByFirebaseUidResponse(
    Guid ProviderAuthIdentityId,
    Guid? ProviderId,
    string FirebaseUserId,
    string Email,
    bool IsEmailVerified,
    string? DisplayName,
    string SignUpStatus,
    bool HasProfile,
    string? OnboardingStatus,
    DateTimeOffset? MobileVerifiedAtUtc,
    bool? IsActive);

/// <summary>
/// Outcome of a provider account delete. The delete anonymises the provider and
/// disables the account — it does not remove the row — so the retained counts
/// report the history that deliberately survived.
/// </summary>
public sealed record DeleteProviderAccountResponse(
    Guid ProviderId,
    DateTimeOffset DeletedAtUtc,
    // True when the account was already deleted — the call is idempotent and
    // this is the original DeletedAtUtc, not a fresh one.
    bool WasAlreadyDeleted,
    int DeactivatedServiceCount,
    int RetainedBookingCount,
    int RetainedNightStayBookingCount,
    int RetainedEventCount,
    int RetainedPaymentCount);

/// <param name="AcknowledgeExistingBookings">
/// "Honour bookings &amp; deactivate". Omitted/false keeps the historic
/// behaviour: a deactivation with future bookings is refused and they come back
/// as <c>conflictingBookings</c>. True says the provider has seen that list and
/// commits to serving those jobs — the switch flips for NEW bookings only and
/// the existing ones carry on as normal. Ignored when activating.
/// Carries a C# default so it stays out of the OpenAPI <c>required</c> list.
/// </param>
public sealed record SetProviderActiveStatusRequest(
    bool IsActive,
    bool AcknowledgeExistingBookings = false);

public enum SetProviderActiveStatusResult
{
    Updated,
    BookingsExist
}

public sealed record ActiveStatusConflictingBookingSummary(
    Guid BookingId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    // Null for Source = 'Custom' (provider-added private job).
    Guid? PetParentId,
    string Source,
    string? CustomerName,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

/// <param name="HonouredBookingCount">
/// How many future bookings the provider just committed to serving — non-zero
/// only on a deactivation that carried <c>acknowledgeExistingBookings</c>. Null
/// on the <c>BookingsExist</c> outcome (nothing was flipped).
/// </param>
public sealed record SetProviderActiveStatusResponse(
    SetProviderActiveStatusResult Status,
    Guid ProviderId,
    bool? IsActive,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<ActiveStatusConflictingBookingSummary>? ConflictingBookings,
    string? WarningMessage,
    int? HonouredBookingCount);

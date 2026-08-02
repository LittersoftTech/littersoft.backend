using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Application.ProviderOnboarding;

public interface IProviderOnboardingService
{
    Task<ProviderFirebaseAuthResponse> SaveFirebaseAuthAsync(
        SaveProviderFirebaseAuthCommand command,
        CancellationToken cancellationToken);

    Task<ProviderProfileResponse> CompleteProviderProfileAsync(
        CompleteProviderProfileRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the persisted personal information for a provider (name, gender,
    /// mobile, date of birth, mobile-verified timestamp, onboarding status,
    /// timestamps). Throws <see cref="ProviderProfileNotFoundException"/> when the
    /// row is missing.
    /// </summary>
    Task<ProviderProfileResponse> GetProviderProfileAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates the provider's personal details (first name, last name, gender,
    /// date of birth) and returns the persisted row. The mobile number is not
    /// editable here — a change there must go back through OTP verification.
    /// Throws <see cref="ProviderProfileNotFoundException"/> when the row is
    /// missing, <see cref="ProviderAccountDeletedException"/> when the account has
    /// been deleted (an edit would undo the anonymisation), and
    /// <see cref="UnsupportedGenderException"/> for an unknown gender.
    /// </summary>
    Task<ProviderProfileResponse> UpdateProviderProfileAsync(
        Guid providerId,
        UpdateProviderProfileRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Anonymises the provider's SQL footprint: scrubs the personal fields on the
    /// profile row, severs the Firebase auth identity, forces the account
    /// permanently disabled, deactivates the bookable services, and clears the
    /// operational config (availability, closures, policies, payout methods,
    /// media, device tokens, OTPs). Bookings, night-stay bookings, organised
    /// events and the <c>Booking.BookingPayments</c> ledger are all
    /// <b>retained</b> — the ProviderId stays valid.
    /// Returns the keys of the Cosmos listing and blobs the caller must still
    /// clean up; prefer <see cref="IProviderAccountService.DeleteAsync"/>, which
    /// does that for you. Throws <see cref="ProviderProfileNotFoundException"/>
    /// when the provider row is missing.
    /// </summary>
    Task<ProviderAccountDeletionResult> DeleteProviderAccountAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    Task<SendProviderMobileOtpResponse> SendProviderMobileOtpAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    Task<VerifyProviderMobileOtpResponse> VerifyProviderMobileOtpAsync(
        Guid providerId,
        Guid providerMobileOtpId,
        VerifyProviderMobileOtpRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a Firebase user id to the associated provider auth identity and
    /// (if one exists) the provider profile. Used by the mobile app after a
    /// reinstall to recover its ProviderId from the current Firebase session.
    /// Throws <see cref="ProviderAuthIdentityForFirebaseUserNotFoundException"/>
    /// when no auth identity exists for the Firebase user id.
    /// </summary>
    Task<ResolveProviderByFirebaseUidResponse> ResolveProviderByFirebaseUidAsync(
        string firebaseUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Toggles the provider's master Active/Inactive switch. Deactivation is
    /// rejected (returns the <see cref="SetActiveStatusOutcome.BookingsExist"/>
    /// variant) when future confirmed bookings exist on any of the provider's
    /// services — caller must move/cancel them and retry. Activation is always
    /// applied. Throws <see cref="ProviderProfileNotFoundException"/> when the
    /// provider row is missing.
    /// </summary>
    Task<SetActiveStatusOutcome> SetActiveStatusAsync(
        Guid providerId,
        bool isActive,
        CancellationToken cancellationToken);
}

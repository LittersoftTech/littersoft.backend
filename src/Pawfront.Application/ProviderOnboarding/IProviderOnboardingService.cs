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

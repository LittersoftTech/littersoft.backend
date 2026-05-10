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

    Task<SendProviderMobileOtpResponse> SendProviderMobileOtpAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    Task<VerifyProviderMobileOtpResponse> VerifyProviderMobileOtpAsync(
        Guid providerId,
        Guid providerMobileOtpId,
        VerifyProviderMobileOtpRequest request,
        CancellationToken cancellationToken);
}

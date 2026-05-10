namespace Pawfront.Application.Onboarding;

public interface IProviderOnboardingStatusService
{
    Task<ProviderOnboardingStatus> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

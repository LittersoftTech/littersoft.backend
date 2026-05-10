namespace Pawfront.Application.Onboarding;

public interface IProviderOnboardingStatusReader
{
    /// <summary>
    /// Reads the SQL-side onboarding state for a provider.
    /// Returns null if the provider does not exist.
    /// </summary>
    Task<ProviderOnboardingStatusSnapshot?> ReadAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

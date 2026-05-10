namespace Pawfront.Application.Policies;

public interface IProviderPolicyService
{
    Task<ProviderPolicyResult> SavePayoutMethodsAsync(
        Guid providerId,
        IReadOnlyCollection<string> payoutMethods,
        CancellationToken cancellationToken);

    Task<ProviderPolicyResult> SaveCancellationPolicyAsync(
        Guid providerId,
        int? minimumHoursBeforeCancellation,
        CancellationToken cancellationToken);

    Task<ProviderPolicyResult> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

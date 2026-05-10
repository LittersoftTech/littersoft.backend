namespace Pawfront.Contracts.Policies;

public sealed record SaveProviderPayoutMethodsRequest(
    IReadOnlyCollection<string> PayoutMethods);

public sealed record SaveProviderCancellationPolicyRequest(
    int? MinimumHoursBeforeCancellation);

public sealed record ProviderPolicyResponse(
    Guid ProviderId,
    IReadOnlyCollection<string> PayoutMethods,
    int? MinimumHoursBeforeCancellation,
    DateTimeOffset? PolicyUpdatedAtUtc);

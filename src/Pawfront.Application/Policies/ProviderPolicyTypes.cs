namespace Pawfront.Application.Policies;

public sealed record ProviderPolicyResult(
    Guid ProviderId,
    IReadOnlyCollection<string> PayoutMethods,
    int? MinimumHoursBeforeCancellation,
    DateTimeOffset? PolicyUpdatedAtUtc);

public static class ProviderPayoutMethods
{
    public const string Cash = "Cash";
    public const string Digital = "Digital";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Cash, Digital
    };
}

public static class ProviderCancellationPolicyHours
{
    /// <summary>Hours that are allowed when a policy is set. Null means "no policy".</summary>
    public static readonly IReadOnlySet<int> Allowed = new HashSet<int> { 24, 48, 72, 96 };
}

public sealed class ProviderPolicyProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

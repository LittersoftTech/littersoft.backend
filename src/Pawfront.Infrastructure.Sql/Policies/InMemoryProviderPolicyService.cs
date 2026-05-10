using System.Collections.Concurrent;
using Pawfront.Application.Policies;

namespace Pawfront.Infrastructure.Sql.Policies;

internal sealed class InMemoryProviderPolicyService : IProviderPolicyService
{
    private readonly ConcurrentDictionary<Guid, PolicyState> states = new();

    public Task<ProviderPolicyResult> SavePayoutMethodsAsync(
        Guid providerId,
        IReadOnlyCollection<string> payoutMethods,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePayoutMethods(payoutMethods);
        var now = DateTimeOffset.UtcNow;

        var state = states.AddOrUpdate(
            providerId,
            _ => new PolicyState { PayoutMethods = normalized, UpdatedAtUtc = now },
            (_, existing) =>
            {
                existing.PayoutMethods = normalized;
                existing.UpdatedAtUtc = now;
                return existing;
            });

        return Task.FromResult(ToResult(providerId, state));
    }

    public Task<ProviderPolicyResult> SaveCancellationPolicyAsync(
        Guid providerId,
        int? minimumHoursBeforeCancellation,
        CancellationToken cancellationToken)
    {
        ValidateCancellationHours(minimumHoursBeforeCancellation);
        var now = DateTimeOffset.UtcNow;

        var state = states.AddOrUpdate(
            providerId,
            _ => new PolicyState { CancellationHours = minimumHoursBeforeCancellation, UpdatedAtUtc = now },
            (_, existing) =>
            {
                existing.CancellationHours = minimumHoursBeforeCancellation;
                existing.UpdatedAtUtc = now;
                return existing;
            });

        return Task.FromResult(ToResult(providerId, state));
    }

    public Task<ProviderPolicyResult> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        return Task.FromResult(states.TryGetValue(providerId, out var state)
            ? ToResult(providerId, state)
            : new ProviderPolicyResult(providerId, Array.Empty<string>(), null, null));
    }

    private static List<string> NormalizePayoutMethods(IReadOnlyCollection<string>? payoutMethods)
    {
        if (payoutMethods is null)
        {
            throw new ArgumentException("Payout methods are required.", nameof(payoutMethods));
        }

        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in payoutMethods)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!ProviderPayoutMethods.Allowed.Contains(trimmed))
            {
                throw new ArgumentException(
                    $"Payout method '{trimmed}' is not supported.",
                    nameof(payoutMethods));
            }

            if (seen.Add(trimmed))
            {
                deduped.Add(trimmed);
            }
        }

        if (deduped.Count == 0)
        {
            throw new ArgumentException(
                "At least one payout method must be selected.",
                nameof(payoutMethods));
        }

        deduped.Sort(StringComparer.Ordinal);
        return deduped;
    }

    private static void ValidateCancellationHours(int? hours)
    {
        if (hours.HasValue && !ProviderCancellationPolicyHours.Allowed.Contains(hours.Value))
        {
            throw new ArgumentException(
                $"Cancellation policy hours '{hours.Value}' is not supported. Allowed: 24, 48, 72, 96, or null for no policy.",
                nameof(hours));
        }
    }

    private static ProviderPolicyResult ToResult(Guid providerId, PolicyState state)
    {
        return new ProviderPolicyResult(
            providerId,
            state.PayoutMethods.ToArray(),
            state.CancellationHours,
            state.UpdatedAtUtc);
    }

    private sealed class PolicyState
    {
        public List<string> PayoutMethods { get; set; } = new();
        public int? CancellationHours { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
    }
}

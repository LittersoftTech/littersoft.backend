using Pawfront.Application.Reliability;
using Pawfront.Contracts.Reliability;

namespace Pawfront.Infrastructure.Sql.Reliability;

/// <summary>No SQL outcome history is available in the development fallback.</summary>
internal sealed class NullReliabilityReader : IReliabilityReader
{
    public Task<ReliabilityResponse> GetParentAsync(Guid petParentId, CancellationToken ct) => Empty(true);
    public Task<ReliabilityResponse> GetProviderAsync(Guid providerId, CancellationToken ct) => Empty(false);

    private static Task<ReliabilityResponse> Empty(bool parent)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new ReliabilityResponse(parent ? ServiceReliabilityCalculator.Calculate([], now)
            : ServiceReliabilityCalculator.NotApplicable(), new(false, null, 0, 0, 0, true, 5, 0, 5),
            new(false, 0, 0, now.AddMonths(-12)), now));
    }
}

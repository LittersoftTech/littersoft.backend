using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;

namespace Pawfront.Application.Jobs;

/// <summary>
/// Composes the provider job-list read. Thin by design, the same way
/// <c>ProviderEarningsService</c> is: the filtering, the money and the ordering
/// all live in <c>Booking.ListProviderJobs</c>, so this layer only supplies the
/// configured platform fee percentage and clamps paging.
/// </summary>
internal sealed class ProviderJobService(
    IProviderJobStore store,
    IOptions<PawfrontFeeOptions> feeOptions) : IProviderJobService
{
    /// <summary>
    /// Hard cap on page size, matching the earnings, spend and review lists — a
    /// hand-written request must not be able to pull a provider's whole history in
    /// one go.
    /// </summary>
    public const int MaxPageSize = 20;

    public Task<ProviderJobPage> ListAsync(
        ProviderJobQuery query,
        CancellationToken cancellationToken)
    {
        var clamped = query with
        {
            Skip = Math.Max(0, query.Skip),
            Take = Math.Clamp(query.Take <= 0 ? MaxPageSize : query.Take, 1, MaxPageSize)
        };

        return store.ListAsync(
            clamped, feeOptions.Value.PawfrontFeePercentage, cancellationToken);
    }
}

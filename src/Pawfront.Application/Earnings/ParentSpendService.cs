using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;

namespace Pawfront.Application.Earnings;

/// <summary>
/// Composes the pet-parent booking summary + history reads. Mirror of
/// <see cref="ProviderEarningsService"/>: period resolution, the configured fee
/// percentage, and paging clamps — the arithmetic itself is shared in SQL.
/// </summary>
internal sealed class ParentSpendService(
    IParentSpendStore store,
    IOptions<PawfrontFeeOptions> feeOptions) : IParentSpendService
{
    public async Task<ParentBookingSummaryResult> GetSummaryAsync(
        ParentBookingHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = ProviderEarningsService.ResolveRange(query.Period, query.FromDate, query.ToDate);

        var summary = await store.GetSummaryAsync(
            query.PetParentId,
            from,
            to,
            query.PetId,
            query.Statuses,
            feeOptions.Value.PawfrontFeePercentage,
            cancellationToken);

        return new ParentBookingSummaryResult(query.Period, from, to, summary);
    }

    public async Task<PagedEarningsResult<ParentBookingHistoryRow>> ListAsync(
        ParentBookingHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = ProviderEarningsService.ResolveRange(query.Period, query.FromDate, query.ToDate);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(
            query.Take <= 0 ? ProviderEarningsService.MaxPageSize : query.Take,
            1,
            ProviderEarningsService.MaxPageSize);

        var (items, total) = await store.ListAsync(
            query.PetParentId,
            from,
            to,
            query.PetId,
            query.Statuses,
            feeOptions.Value.PawfrontFeePercentage,
            query.SortBy,
            query.SortDirection,
            skip,
            take,
            cancellationToken);

        return new PagedEarningsResult<ParentBookingHistoryRow>(items, total, skip, take, from, to);
    }
}

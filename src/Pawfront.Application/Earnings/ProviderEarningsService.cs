using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;

namespace Pawfront.Application.Earnings;

/// <summary>
/// Composes the provider earnings reads. Thin by design: the arithmetic lives in
/// SQL (one definition shared with the parent side), so this layer only resolves
/// calendar periods, supplies the configured platform fee percentage, and clamps
/// paging.
/// </summary>
internal sealed class ProviderEarningsService(
    IProviderEarningsStore store,
    IOptions<PawfrontFeeOptions> feeOptions) : IProviderEarningsService
{
    /// <summary>
    /// Hard cap on page size. The product requirement is 20 per page; enforcing it
    /// server-side means a hand-written request can't pull the whole ledger.
    /// </summary>
    public const int MaxPageSize = 20;

    public async Task<ProviderEarningsOverview> GetOverviewAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        // One "today" for all four reads — resolving it per call would let a request
        // that straddles midnight UTC mix two different weeks into one screen.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fee = feeOptions.Value.PawfrontFeePercentage;

        var allTime = await store.GetSummaryAsync(providerId, null, null, fee, cancellationToken);
        var week = await GetForRangeAsync(providerId, EarningsPeriod.Weekly, today, fee, cancellationToken);
        var month = await GetForRangeAsync(providerId, EarningsPeriod.Monthly, today, fee, cancellationToken);
        var year = await GetForRangeAsync(providerId, EarningsPeriod.Yearly, today, fee, cancellationToken);

        return new ProviderEarningsOverview(allTime, week, month, year);
    }

    public Task<ProviderEarningsPeriodResult> GetForPeriodAsync(
        Guid providerId,
        EarningsPeriod period,
        CancellationToken cancellationToken)
        => GetForRangeAsync(
            providerId,
            period,
            DateOnly.FromDateTime(DateTime.UtcNow),
            feeOptions.Value.PawfrontFeePercentage,
            cancellationToken);

    public async Task<PagedEarningsResult<ProviderEarningsBookingRow>> ListBookingsAsync(
        ProviderEarningsBookingQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(query.Period, query.FromDate, query.ToDate);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? MaxPageSize : query.Take, 1, MaxPageSize);

        var (items, total) = await store.ListBookingsAsync(
            query.ProviderId,
            from,
            to,
            query.Statuses,
            feeOptions.Value.PawfrontFeePercentage,
            query.SortBy,
            query.SortDirection,
            skip,
            take,
            cancellationToken);

        return new PagedEarningsResult<ProviderEarningsBookingRow>(items, total, skip, take, from, to);
    }

    private async Task<ProviderEarningsPeriodResult> GetForRangeAsync(
        Guid providerId,
        EarningsPeriod period,
        DateOnly today,
        decimal feePercentage,
        CancellationToken cancellationToken)
    {
        var (from, to) = EarningsPeriodRange.Resolve(period, today);
        var totals = await store.GetSummaryAsync(providerId, from, to, feePercentage, cancellationToken);
        return new ProviderEarningsPeriodResult(period, from, to, totals);
    }

    /// <summary>
    /// Explicit dates win over the period. A caller that supplies only one bound
    /// gets an open-ended range on the other side rather than having the period
    /// silently fill it in — mixing the two would produce a range the caller never
    /// asked for.
    /// </summary>
    internal static (DateOnly? From, DateOnly? To) ResolveRange(
        EarningsPeriod period,
        DateOnly? explicitFrom,
        DateOnly? explicitTo)
    {
        if (explicitFrom is not null || explicitTo is not null)
        {
            return (explicitFrom, explicitTo);
        }

        return EarningsPeriodRange.Resolve(period, DateOnly.FromDateTime(DateTime.UtcNow));
    }
}

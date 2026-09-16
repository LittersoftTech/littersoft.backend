using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;

namespace Pawfront.Application.Analytics;

/// <summary>
/// Composes the provider analytics reads. Thin by design, for the same reason
/// <see cref="ProviderEarningsService"/> is: the aggregation lives in SQL (one
/// definition shared with the earnings summary), so this layer only resolves
/// calendar periods, supplies the configured platform fee percentage, and clamps
/// paging.
/// </summary>
internal sealed class ProviderAnalyticsService(
    IProviderServiceViewStore viewStore,
    IProviderServiceBreakdownStore breakdownStore,
    IOptions<PawfrontFeeOptions> feeOptions) : IProviderAnalyticsService
{
    public Task<ProviderViewRecord> RecordViewAsync(
        RecordProviderViewCommand command,
        CancellationToken cancellationToken)
        => viewStore.RecordAsync(command, cancellationToken);

    public async Task<ProviderViewSummary> GetViewSummaryAsync(
        Guid providerId,
        EarningsPeriod period,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        // Same rule the earnings reads follow: explicit dates win over the period,
        // and a caller supplying one bound gets an open-ended range on the other
        // rather than having the period silently fill it in.
        var (from, to) = ProviderEarningsService.ResolveRange(period, fromDate, toDate);

        var (totals, services) = await viewStore.GetSummaryAsync(
            providerId, from, to, cancellationToken);

        return new ProviderViewSummary(period, from, to, totals, services);
    }

    public async Task<PagedEarningsResult<ProviderServiceViewerRow>> ListViewersAsync(
        ProviderServiceViewerQuery query,
        CancellationToken cancellationToken)
    {
        var (from, to) = ProviderEarningsService.ResolveRange(
            query.Period, query.FromDate, query.ToDate);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(
            query.Take <= 0 ? ProviderEarningsService.MaxPageSize : query.Take,
            1,
            ProviderEarningsService.MaxPageSize);

        var (items, total) = await viewStore.ListViewersAsync(
            query.ProviderId, query.ServiceId, from, to, skip, take, cancellationToken);

        return new PagedEarningsResult<ProviderServiceViewerRow>(items, total, skip, take, from, to);
    }

    public async Task<ProviderBookingBreakdown> GetBookingBreakdownAsync(
        Guid providerId,
        EarningsPeriod period,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        var (from, to) = ProviderEarningsService.ResolveRange(period, fromDate, toDate);

        var (totals, services) = await breakdownStore.GetByServiceAsync(
            providerId, from, to, feeOptions.Value.PawfrontFeePercentage, cancellationToken);

        return new ProviderBookingBreakdown(period, from, to, totals, services);
    }
}

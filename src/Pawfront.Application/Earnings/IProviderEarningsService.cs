namespace Pawfront.Application.Earnings;

/// <summary>
/// Read-only earnings reporting for a provider. Kept separate from
/// <c>IBookingService</c> (transactional booking flows) — nothing here mutates.
/// </summary>
public interface IProviderEarningsService
{
    /// <summary>
    /// Lifetime totals plus this week / month / year, for the earnings landing
    /// screen. One call rather than four so the tiles can never be computed from
    /// four different instants.
    /// </summary>
    Task<ProviderEarningsOverview> GetOverviewAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same totals scoped to one calendar period, with the resolved date range
    /// echoed back so the client can label the screen without recomputing it.
    /// </summary>
    Task<ProviderEarningsPeriodResult> GetForPeriodAsync(
        Guid providerId,
        EarningsPeriod period,
        CancellationToken cancellationToken);

    /// <summary>
    /// Booking-level breakdown of the earnings figure — which jobs produced what.
    /// Reconciles exactly with <see cref="GetForPeriodAsync"/> over the same range,
    /// because both read the same underlying definition of a booking's amount.
    /// </summary>
    Task<PagedEarningsResult<ProviderEarningsBookingRow>> ListBookingsAsync(
        ProviderEarningsBookingQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader behind <see cref="IProviderEarningsService"/>. The fee
/// percentage is passed in rather than read here: it lives in configuration
/// (<c>Payments:PawfrontFeePercentage</c>) and only the Application layer should
/// know that.
/// </summary>
public interface IProviderEarningsStore
{
    Task<ProviderEarningsTotals> GetSummaryAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        decimal feePercentage,
        CancellationToken cancellationToken);

    /// <param name="statuses">
    /// Raw lifecycle statuses to include, already expanded from the caller's groups
    /// by <see cref="BookingStatusFilter.Expand"/>. Empty means no status filter,
    /// which the store reads as the earned rows only.
    /// </param>
    Task<(IReadOnlyList<ProviderEarningsBookingRow> Items, int TotalCount)> ListBookingsAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        EarningsSortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        CancellationToken cancellationToken);
}

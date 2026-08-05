namespace Pawfront.Application.Earnings;

/// <summary>
/// Read-only booking history + spend reporting for a pet parent. The mirror of
/// <see cref="IProviderEarningsService"/>, reading the same underlying amounts so
/// a parent's "spent" always matches the provider's "earned" on a given booking.
/// </summary>
public interface IParentSpendService
{
    /// <summary>
    /// Counts and spend for the parent's bookings, narrowed by the same filters the
    /// history list uses — so the summary always describes exactly the set the list
    /// is showing.
    /// </summary>
    Task<ParentBookingSummaryResult> GetSummaryAsync(
        ParentBookingHistoryQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Paginated booking history — single-day and night-stay merged into one feed,
    /// including cancelled and upcoming bookings.
    /// </summary>
    Task<PagedEarningsResult<ParentBookingHistoryRow>> ListAsync(
        ParentBookingHistoryQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader behind <see cref="IParentSpendService"/>. <paramref name="statuses"/>
/// is already expanded to raw lifecycle statuses by
/// <see cref="ParentBookingStatusFilter"/>; an empty list means no status filter.
/// </summary>
public interface IParentSpendStore
{
    Task<ParentBookingSummary> GetSummaryAsync(
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<ParentBookingHistoryRow> Items, int TotalCount)> ListAsync(
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        ParentHistorySortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        CancellationToken cancellationToken);
}

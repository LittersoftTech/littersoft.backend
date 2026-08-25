using Pawfront.Application.Earnings;

namespace Pawfront.Infrastructure.Sql.Earnings;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderEarningsStore"/> (registered
/// only when there is no SQL connection and Key Vault is disabled). Reports zero
/// earnings.
/// </summary>
/// <remarks>
/// Earnings are aggregates over the booking tables joined to the payment ledger;
/// the in-memory booking stores hold neither the ledger nor the payout columns, so
/// there is nothing meaningful to compute. Returning empty is the same posture the
/// booking sweeps take in memory (they simply never run) — a developer on the
/// in-memory store sees the endpoints answer 200 with zeros rather than a 500,
/// and real numbers require pointing at SQL.
/// </remarks>
internal sealed class NullProviderEarningsStore : IProviderEarningsStore
{
    public Task<ProviderEarningsTotals> GetSummaryAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        decimal feePercentage,
        CancellationToken cancellationToken)
        => Task.FromResult(ProviderEarningsTotals.Empty);

    public Task<(IReadOnlyList<ProviderEarningsBookingRow> Items, int TotalCount)> ListBookingsAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        EarningsSortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        CancellationToken cancellationToken)
        => Task.FromResult<(IReadOnlyList<ProviderEarningsBookingRow>, int)>(
            (Array.Empty<ProviderEarningsBookingRow>(), 0));
}

/// <summary>
/// In-memory-mode fallback for <see cref="IParentSpendStore"/>. Same reasoning as
/// <see cref="NullProviderEarningsStore"/>.
/// </summary>
internal sealed class NullParentSpendStore : IParentSpendStore
{
    public Task<ParentBookingSummary> GetSummaryAsync(
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        CancellationToken cancellationToken)
        => Task.FromResult(ParentBookingSummary.Empty);

    public Task<(IReadOnlyList<ParentBookingHistoryRow> Items, int TotalCount)> ListAsync(
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
        CancellationToken cancellationToken)
        => Task.FromResult<(IReadOnlyList<ParentBookingHistoryRow>, int)>(
            (Array.Empty<ParentBookingHistoryRow>(), 0));
}

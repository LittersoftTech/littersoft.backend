using Pawfront.Application.Earnings;

namespace Pawfront.Application.Analytics;

/// <summary>
/// The provider's PawPrints analytics: three levels per metric — a dashboard
/// figure, a per-service breakdown, and a customer list.
/// </summary>
/// <remarks>
/// <para>
/// Views are answered from <c>Provider.ProviderServiceViews</c>. Bookings and
/// earnings are answered from <c>Booking.BookingAmounts</c>, the same function the
/// earnings summary reads, so a breakdown can never disagree with the total it
/// expands.
/// </para>
/// <para>
/// The customer list for bookings and earnings is NOT here — it is the existing
/// <see cref="IProviderEarningsService.ListBookingsAsync"/>, which gained a
/// ServiceId filter and the customer fields the viewer cards carry. Adding a
/// second paginated booking list would have meant two definitions of a provider's
/// jobs.
/// </para>
/// <para>
/// Read-only except <see cref="RecordViewAsync"/>, which is the ONE write and is
/// reachable only from the parent host: a provider opening their own profile is
/// not a view.
/// </para>
/// </remarks>
public interface IProviderAnalyticsService
{
    /// <summary>
    /// Records one pet parent looking at one provider. Called from the parent
    /// host; without it every figure above would be zero forever.
    /// </summary>
    Task<ProviderViewRecord> RecordViewAsync(
        RecordProviderViewCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// The "Views" card and its per-service breakdown for one range. Explicit
    /// dates win over <paramref name="period"/>, the same rule the earnings reads
    /// follow.
    /// </summary>
    Task<ProviderViewSummary> GetViewSummaryAsync(
        Guid providerId,
        EarningsPeriod period,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Which parents viewed — one row per parent, most recently interested first.
    /// </summary>
    Task<PagedEarningsResult<ProviderServiceViewerRow>> ListViewersAsync(
        ProviderServiceViewerQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// The "Bookings" / "Earnings" cards and their per-service breakdown for one
    /// range. One call returns counts AND money because they come from the same
    /// rows; each card renders the half it shows.
    /// </summary>
    Task<ProviderBookingBreakdown> GetBookingBreakdownAsync(
        Guid providerId,
        EarningsPeriod period,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader/writer for the provider view log
/// (<c>Provider.ProviderServiceViews</c>).
/// </summary>
public interface IProviderServiceViewStore
{
    Task<ProviderViewRecord> RecordAsync(
        RecordProviderViewCommand command,
        CancellationToken cancellationToken);

    /// <summary>Totals plus the per-service breakdown, in one round trip.</summary>
    Task<(ProviderViewTotals Totals, IReadOnlyList<ProviderServiceViewBreakdown> Services)> GetSummaryAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<ProviderServiceViewerRow> Items, int TotalCount)> ListViewersAsync(
        Guid providerId,
        Guid? serviceId,
        DateOnly? fromDate,
        DateOnly? toDate,
        int skip,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader for the per-service bookings + earnings breakdown
/// (<c>Booking.GetProviderBookingsByService</c>).
/// </summary>
/// <remarks>
/// Separate from <see cref="IProviderEarningsStore"/> because two different
/// surfaces consume it — the analytics endpoint and the period-scoped earnings
/// endpoint — and neither owns it. The fee percentage is passed in rather than
/// read here: it lives in configuration and only the Application layer should know
/// that.
/// </remarks>
public interface IProviderServiceBreakdownStore
{
    Task<(ProviderBookingFigures Totals, IReadOnlyList<ProviderServiceBookingBreakdown> Services)> GetByServiceAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        decimal feePercentage,
        CancellationToken cancellationToken);
}

/// <summary>Thrown when the provider a view names does not exist.</summary>
public sealed class ProviderViewProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.")
{
    public Guid ProviderId { get; } = providerId;
}

/// <summary>
/// Thrown when a view names a service that is not one of the provider's. Refused
/// rather than dropped: a stranger's ServiceId would put their views in this
/// provider's breakdown.
/// </summary>
public sealed class ProviderViewInvalidServiceException(Guid serviceId)
    : Exception($"Service '{serviceId}' is not valid for this provider.")
{
    public Guid ServiceId { get; } = serviceId;
}

/// <summary>
/// Thrown when a view names a pet the caller does not own (or has deleted).
/// Refused rather than dropped: another parent's pet would put the wrong breed on
/// a card the provider is about to act on.
/// </summary>
public sealed class ProviderViewInvalidPetException(Guid petId)
    : Exception($"Pet '{petId}' was not found or does not belong to the pet parent.")
{
    public Guid PetId { get; } = petId;
}

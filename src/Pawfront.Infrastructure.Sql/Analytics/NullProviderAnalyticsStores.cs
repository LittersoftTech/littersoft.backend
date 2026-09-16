using Microsoft.Extensions.Logging;
using Pawfront.Application.Analytics;

namespace Pawfront.Infrastructure.Sql.Analytics;

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderServiceViewStore"/> (registered
/// only when there is no SQL connection and Key Vault is disabled).
/// </summary>
/// <remarks>
/// <para>
/// ACCEPTS the write and drops it, rather than throwing — the same posture
/// <c>NullBookingLocationService</c> takes. Recording a view is a side effect of
/// browsing, and every provider-profile open on the parent host now performs one;
/// failing it would make the browse flow unusable on a dev machine without a
/// database, for the sake of an analytics row nothing is going to read.
/// </para>
/// <para>
/// The READS report zeros rather than serving what was dropped, which is the
/// earnings posture rather than the blocks/reviews one. A functional in-memory
/// store was the right call for blocks because a block is a decision with
/// consequences a developer needs to see; a viewer list is an aggregate that also
/// needs live parent names, pet breeds and the provider's service catalog joined
/// to it, none of which the in-memory stores keep — so it could only ever return
/// half-populated cards, which is worse than an honest zero.
/// </para>
/// </remarks>
internal sealed class NullProviderServiceViewStore(
    ILogger<NullProviderServiceViewStore> logger) : IProviderServiceViewStore
{
    public Task<ProviderViewRecord> RecordAsync(
        RecordProviderViewCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "In-memory mode: dropping provider view for provider {ProviderId} (service {ServiceId}).",
            command.ProviderId,
            command.ServiceId);

        return Task.FromResult(new ProviderViewRecord(
            ProviderServiceViewId: Guid.NewGuid(),
            ProviderId: command.ProviderId,
            ServiceId: command.ServiceId,
            PetParentId: command.PetParentId,
            PetId: command.PetId,
            Source: command.Source,
            ViewedAtUtc: DateTimeOffset.UtcNow));
    }

    public Task<(ProviderViewTotals Totals, IReadOnlyList<ProviderServiceViewBreakdown> Services)>
        GetSummaryAsync(
            Guid providerId,
            DateOnly? fromDate,
            DateOnly? toDate,
            CancellationToken cancellationToken)
        => Task.FromResult<(ProviderViewTotals, IReadOnlyList<ProviderServiceViewBreakdown>)>(
            (ProviderViewTotals.Empty, Array.Empty<ProviderServiceViewBreakdown>()));

    public Task<(IReadOnlyList<ProviderServiceViewerRow> Items, int TotalCount)> ListViewersAsync(
        Guid providerId,
        Guid? serviceId,
        DateOnly? fromDate,
        DateOnly? toDate,
        int skip,
        int take,
        CancellationToken cancellationToken)
        => Task.FromResult<(IReadOnlyList<ProviderServiceViewerRow>, int)>(
            (Array.Empty<ProviderServiceViewerRow>(), 0));
}

/// <summary>
/// In-memory-mode fallback for <see cref="IProviderServiceBreakdownStore"/>.
/// Reports zeros and no services, for exactly the reasons
/// <c>NullProviderEarningsStore</c> does: the figures are aggregates over the
/// booking tables joined to the payment ledger, and the in-memory stores hold
/// neither the ledger nor the payout columns.
/// </summary>
internal sealed class NullProviderServiceBreakdownStore : IProviderServiceBreakdownStore
{
    public Task<(ProviderBookingFigures Totals, IReadOnlyList<ProviderServiceBookingBreakdown> Services)>
        GetByServiceAsync(
            Guid providerId,
            DateOnly? fromDate,
            DateOnly? toDate,
            decimal feePercentage,
            CancellationToken cancellationToken)
        => Task.FromResult<(ProviderBookingFigures, IReadOnlyList<ProviderServiceBookingBreakdown>)>(
            (ProviderBookingFigures.Empty, Array.Empty<ProviderServiceBookingBreakdown>()));
}

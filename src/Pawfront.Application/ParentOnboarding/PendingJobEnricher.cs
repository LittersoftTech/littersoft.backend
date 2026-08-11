using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Application.Offerings;
using Pawfront.Application.Providers;
using Pawfront.Domain.Services;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Fills in the two things the delete sprocs cannot: each pending job's provider
/// photo and its price block.
/// </summary>
/// <remarks>
/// <para>
/// Both refusals — the account delete and the per-pet delete — hand back the same
/// job shape, so they share this one enricher rather than each growing their own
/// near-copy of the pricing arithmetic.
/// </para>
/// <para>
/// The photo has to come from the provider's Cosmos offering document (the SQL
/// provider row has no photo column), and the price needs the offering too
/// whenever a legacy row froze no rate — so one lookup per distinct provider
/// serves both, cached per call because a parent's unfinished jobs are often with
/// the same provider.
/// </para>
/// <para>
/// Every lookup is best-effort: this runs on a path that has ALREADY decided to
/// refuse the delete, and failing the request over a missing offering document
/// would replace a clear "settle these jobs first" with an opaque 500. An
/// unresolvable provider yields a null photo and a price block with null amounts,
/// never an exception.
/// </para>
/// </remarks>
public interface IPendingJobEnricher
{
    Task<IReadOnlyList<PendingParentJob>> EnrichAsync(
        IReadOnlyList<PendingParentJob> jobs,
        CancellationToken cancellationToken);
}

internal sealed class PendingJobEnricher(
    IProviderDiscoveryService discovery,
    IProviderOfferingResolver offeringResolver,
    IOptions<PawfrontFeeOptions> feeOptions) : IPendingJobEnricher
{
    public async Task<IReadOnlyList<PendingParentJob>> EnrichAsync(
        IReadOnlyList<PendingParentJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return jobs;
        }

        // Keyed by (provider, category) like the "my bookings" enrichment: the
        // Cosmos offering document is partitioned by service category, so the pair
        // is what identifies it.
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var offeringCache = new Dictionary<Guid, OfferingResolution.Resolved?>();

        var enriched = new List<PendingParentJob>(jobs.Count);
        foreach (var job in jobs)
        {
            var provider = await GetProviderAsync(
                providerCache, job.ProviderId, job.ServiceCategory, cancellationToken);

            enriched.Add(job with
            {
                ProviderProfilePhotoUrl = provider?.ImageUrl,
                Price = await ResolvePriceAsync(offeringCache, job, cancellationToken)
            });
        }

        return enriched;
    }

    private async Task<PendingJobPrice> ResolvePriceAsync(
        Dictionary<Guid, OfferingResolution.Resolved?> offeringCache,
        PendingParentJob job,
        CancellationToken cancellationToken)
    {
        var isNightStay = string.Equals(job.BookingType, "NightStay", StringComparison.Ordinal);

        // The rate is the one frozen onto the booking at creation, so a later
        // change by the provider never re-prices a job the parent already holds.
        // Only a legacy row that froze nothing falls through to the live offering.
        var unitPrice = job.SnapshotUnitPrice;
        OfferingResolution.Resolved? offering = null;
        if (unitPrice is null)
        {
            offering = await GetOfferingAsync(offeringCache, job.ServiceId, cancellationToken);
            unitPrice = offering?.ServiceType == ProviderServiceTypes.GroomingSession
                ? await ResolveGroomingPriceAsync(job, cancellationToken)
                : offering?.Price;
        }

        var unit = ResolvePriceUnit(isNightStay, job.ServiceCategory);
        var total = ComputeTotal(unitPrice, unit, job, isNightStay);

        // Always the platform rate: a pending job was found by PetParentId, and a
        // Custom walk-in (the only zero-commission case) has none.
        var feePercentage = feeOptions.Value.PawfrontFeePercentage;
        var fee = total is null
            ? (decimal?)null
            : Math.Round(total.Value * feePercentage / 100m, 2, MidpointRounding.AwayFromZero);

        return new PendingJobPrice(unitPrice, unit, total, fee, feePercentage);
    }

    /// <summary>
    /// The unit follows from the booking kind and the service category, so it is
    /// known even when the amount is not. Kept in step with how each category is
    /// actually billed by <c>BookingService.GetDetailAsync</c>: pet sitters charge
    /// by the hour (or the night), everything else is a flat fee.
    /// </summary>
    private static string ResolvePriceUnit(bool isNightStay, string serviceCategory)
    {
        if (isNightStay)
        {
            return PendingJobPriceUnits.PerNight;
        }

        return serviceCategory switch
        {
            nameof(ProviderServiceCategory.PetSitter) => PendingJobPriceUnits.PerHour,
            nameof(ProviderServiceCategory.PetGroomer) => PendingJobPriceUnits.PerService,
            nameof(ProviderServiceCategory.Vet) => PendingJobPriceUnits.PerAppointment,
            nameof(ProviderServiceCategory.PetTrainer) => PendingJobPriceUnits.PerSession,
            // No other category has a bookable service, so this is unreachable in
            // practice; per-hour is the neutral answer rather than a throw on a
            // path that is only reporting why a delete was refused.
            _ => PendingJobPriceUnits.PerHour
        };
    }

    /// <summary>
    /// Rate times quantity for the two per-unit services, the flat rate for the
    /// rest — the same rule <c>BookingService.ResolveAppPricingAsync</c> and
    /// <c>NightStayBookingService.GetDetailAsync</c> apply.
    /// </summary>
    private static decimal? ComputeTotal(
        decimal? unitPrice,
        string unit,
        PendingParentJob job,
        bool isNightStay)
    {
        if (unitPrice is not decimal rate)
        {
            return null;
        }

        if (isNightStay)
        {
            // [CheckInDate, CheckOutDate) — the checkout day is not a stayed night.
            var nights = job.CheckOutDate is DateOnly checkOut
                ? checkOut.DayNumber - job.ServiceDate.DayNumber
                : 0;
            return nights <= 0
                ? null
                : Math.Round(rate * nights, 2, MidpointRounding.AwayFromZero);
        }

        if (unit != PendingJobPriceUnits.PerHour)
        {
            return Math.Round(rate, 2, MidpointRounding.AwayFromZero);
        }

        if (job.StartTime is not TimeOnly start || job.EndTime is not TimeOnly end || end <= start)
        {
            return null;
        }

        var hours = (decimal)(end - start).TotalHours;
        return Math.Round(rate * hours, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// A groomer's rate is per menu item, so a legacy row with no frozen rate has
    /// to be priced from the item the booking names.
    /// </summary>
    private async Task<decimal?> ResolveGroomingPriceAsync(
        PendingParentJob job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.ServiceItemCode))
        {
            return null;
        }

        try
        {
            var item = await offeringResolver.ResolveGroomingItemAsync(
                job.ProviderId, job.ServiceItemCode!, cancellationToken);
            return item is GroomingItemResolution.Resolved resolved ? resolved.Price : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProviderSummary?> GetProviderAsync(
        Dictionary<(Guid, string), ProviderSummary?> cache,
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        var key = (providerId, serviceCategory);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ProviderSummary? summary = null;
        try
        {
            summary = await discovery.GetSummaryAsync(providerId, serviceCategory, cancellationToken);
        }
        catch
        {
            // Best-effort — a missing offering document leaves the photo null.
        }

        cache[key] = summary;
        return summary;
    }

    private async Task<OfferingResolution.Resolved?> GetOfferingAsync(
        Dictionary<Guid, OfferingResolution.Resolved?> cache,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(serviceId, out var cached))
        {
            return cached;
        }

        OfferingResolution.Resolved? resolved = null;
        try
        {
            resolved = await offeringResolver.ResolveAsync(serviceId, cancellationToken)
                as OfferingResolution.Resolved;
        }
        catch
        {
            // Best-effort — an unresolvable service leaves the price null.
        }

        cache[serviceId] = resolved;
        return resolved;
    }
}

using Pawfront.Application.Offerings;
using Pawfront.Application.Providers;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

/// <summary>
/// Enriches a pet parent's "my bookings" cards with the booked provider's
/// summary (business name / image / city) and the service's price — per hour
/// for single-day services, per night for NightStay. The price PREFERS the rate
/// frozen onto the booking at creation (price-lock) and falls back to the live
/// offering only for legacy rows without a snapshot, so a later rate change by
/// the provider never re-prices an existing card. Best-effort: a failed
/// provider/offering lookup yields nulls for that card rather than failing the
/// whole list. Provider summaries are cached per call to avoid duplicate reads
/// when a parent has several bookings with the same provider.
/// </summary>
public interface IParentBookingEnrichmentService
{
    Task<IReadOnlyList<EnrichedBookingCard>> EnrichAsync(
        IReadOnlyList<BookingListItemResult> bookings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        IReadOnlyList<NightStayBookingListItemResult> bookings,
        CancellationToken cancellationToken);
}

/// <summary>
/// A single-day booking plus its provider summary, service type, per-hour price
/// (snapshot-preferred), and the frozen cancellation-policy + selected-location
/// blocks carried through from the list row.
/// </summary>
/// <param name="ServiceDescription">
/// What the provider says about the booked service — the menu item's blurb for
/// a groomer, the session description for a trainer. Read LIVE (it is cosmetic
/// copy, deliberately not price-locked onto the booking), so a later edit by the
/// provider shows through. Null for the other categories and when unresolvable.
/// </param>
public sealed record EnrichedBookingCard(
    BookingResult Booking,
    ProviderSummary? Provider,
    string? ServiceType,
    decimal? PricePerHour,
    int? CancellationPolicyHours,
    BookingLocationResult Location,
    string? ServiceDescription = null);

/// <summary>
/// A night-stay booking plus its provider summary, per-night price
/// (snapshot-preferred), and the frozen cancellation-policy + selected-location
/// blocks carried through from the list row.
/// </summary>
public sealed record EnrichedNightStayBookingCard(
    NightStayBookingResult Booking,
    ProviderSummary? Provider,
    decimal? PricePerNight,
    int? CancellationPolicyHours,
    BookingLocationResult Location);

internal sealed class ParentBookingEnrichmentService(
    IProviderDiscoveryService discovery,
    IProviderOfferingResolver offeringResolver,
    IProviderNameReader providerNameReader) : IParentBookingEnrichmentService
{
    public async Task<IReadOnlyList<EnrichedBookingCard>> EnrichAsync(
        IReadOnlyList<BookingListItemResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var cards = new List<EnrichedBookingCard>(bookings.Count);
        foreach (var item in bookings)
        {
            var booking = item.Booking;
            var provider = await GetProviderAsync(
                providerCache, booking.ProviderId, booking.ServiceCategory, cancellationToken);
            // The live resolver still supplies the ServiceType label, but the price
            // prefers the rate frozen onto the booking at creation (price-lock) —
            // live is only the legacy-row fallback.
            var (serviceType, livePrice, description) = await ResolvePriceAsync(
                booking.ProviderId, booking.ServiceId, booking.ServiceItemCode, cancellationToken);
            cards.Add(new EnrichedBookingCard(
                booking, provider, serviceType,
                booking.PricePerHour ?? livePrice,
                item.CancellationPolicyHours, item.Location,
                description));
        }

        var names = await ResolveFreelancerNamesAsync(
            cards.Select(c => c.Provider), cancellationToken);
        return cards
            .Select(c => c with { Provider = FillDisplayName(c.Provider, names) })
            .ToList();
    }

    public async Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        IReadOnlyList<NightStayBookingListItemResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var cards = new List<EnrichedNightStayBookingCard>(bookings.Count);
        foreach (var item in bookings)
        {
            var booking = item.Booking;
            var provider = await GetProviderAsync(
                providerCache, booking.ProviderId, booking.ServiceCategory, cancellationToken);
            // Prefer the per-night rate frozen onto the stay at creation
            // (price-lock); resolve the live offering rate only for legacy rows
            // without a snapshot.
            var pricePerNight = item.PricePerNight;
            if (pricePerNight is null)
            {
                (_, pricePerNight, _) = await ResolvePriceAsync(
                    booking.ProviderId, booking.ServiceId, serviceItemCode: null, cancellationToken);
            }
            cards.Add(new EnrichedNightStayBookingCard(
                booking, provider, pricePerNight,
                item.CancellationPolicyHours, item.Location));
        }

        var names = await ResolveFreelancerNamesAsync(
            cards.Select(c => c.Provider), cancellationToken);
        return cards
            .Select(c => c with { Provider = FillDisplayName(c.Provider, names) })
            .ToList();
    }

    /// <summary>
    /// Batch-reads personal names for providers whose offering has no business
    /// name (freelancers) so the card's provider block always has a label.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveFreelancerNamesAsync(
        IEnumerable<ProviderSummary?> providers,
        CancellationToken cancellationToken)
    {
        var providerIds = providers
            .Where(p => p is not null && string.IsNullOrWhiteSpace(p.DisplayName))
            .Select(p => p!.ProviderId)
            .Distinct()
            .ToArray();

        if (providerIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            return await providerNameReader.GetProviderDisplayNamesAsync(providerIds, cancellationToken);
        }
        catch
        {
            // Best-effort — a failed name read leaves freelancer labels null
            // rather than failing the whole bookings list.
            return new Dictionary<Guid, string>();
        }
    }

    /// <summary>
    /// Overlays the resolved personal name onto a provider summary that has no
    /// business name (freelancer). Businesses and unresolved lookups are left as-is.
    /// </summary>
    private static ProviderSummary? FillDisplayName(
        ProviderSummary? provider,
        IReadOnlyDictionary<Guid, string> names)
    {
        if (provider is null || !string.IsNullOrWhiteSpace(provider.DisplayName))
        {
            return provider;
        }

        return names.TryGetValue(provider.ProviderId, out var personName)
            ? provider with { DisplayName = personName }
            : provider;
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
            // Best-effort enrichment — a missing/failed provider doc leaves the
            // provider section unpopulated rather than failing the whole list.
        }

        cache[key] = summary;
        return summary;
    }

    private async Task<(string? ServiceType, decimal? Price, string? Description)> ResolvePriceAsync(
        Guid providerId,
        Guid serviceId,
        string? serviceItemCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await offeringResolver.ResolveAsync(serviceId, cancellationToken);
            if (resolution is not OfferingResolution.Resolved offering)
            {
                return (null, null, null);
            }

            // PetGroomer: price AND description are per menu item — resolve from
            // the booking's code.
            if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
            {
                if (string.IsNullOrWhiteSpace(serviceItemCode))
                {
                    return (offering.ServiceType, null, null);
                }

                var item = await offeringResolver.ResolveGroomingItemAsync(
                    providerId, serviceItemCode!, cancellationToken);
                return item is GroomingItemResolution.Resolved resolved
                    ? (offering.ServiceType, resolved.Price, resolved.Description)
                    : (offering.ServiceType, null, null);
            }

            return (offering.ServiceType, offering.Price, offering.Description);
        }
        catch
        {
            // Best-effort — leave price null if the offering can't be resolved.
            return (null, null, null);
        }
    }
}

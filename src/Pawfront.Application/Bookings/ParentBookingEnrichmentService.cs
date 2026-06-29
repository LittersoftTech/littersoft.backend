using Pawfront.Application.Offerings;
using Pawfront.Application.Providers;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

/// <summary>
/// Enriches a pet parent's "my bookings" cards with the booked provider's
/// summary (business name / image / city) and the service's live price — per
/// hour for single-day services, per night for NightStay. Best-effort: a failed
/// provider/offering lookup yields nulls for that card rather than failing the
/// whole list. Provider summaries are cached per call to avoid duplicate reads
/// when a parent has several bookings with the same provider.
/// </summary>
public interface IParentBookingEnrichmentService
{
    Task<IReadOnlyList<EnrichedBookingCard>> EnrichAsync(
        IReadOnlyList<BookingResult> bookings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        IReadOnlyList<NightStayBookingResult> bookings,
        CancellationToken cancellationToken);
}

/// <summary>A single-day booking plus its provider summary, service type, and per-hour price.</summary>
public sealed record EnrichedBookingCard(
    BookingResult Booking,
    ProviderSummary? Provider,
    string? ServiceType,
    decimal? PricePerHour);

/// <summary>A night-stay booking plus its provider summary and per-night price.</summary>
public sealed record EnrichedNightStayBookingCard(
    NightStayBookingResult Booking,
    ProviderSummary? Provider,
    decimal? PricePerNight);

internal sealed class ParentBookingEnrichmentService(
    IProviderDiscoveryService discovery,
    IProviderOfferingResolver offeringResolver) : IParentBookingEnrichmentService
{
    public async Task<IReadOnlyList<EnrichedBookingCard>> EnrichAsync(
        IReadOnlyList<BookingResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var cards = new List<EnrichedBookingCard>(bookings.Count);
        foreach (var booking in bookings)
        {
            var provider = await GetProviderAsync(
                providerCache, booking.ProviderId, booking.ServiceCategory, cancellationToken);
            var (serviceType, price) = await ResolvePriceAsync(
                booking.ProviderId, booking.ServiceId, booking.ServiceItemCode, cancellationToken);
            cards.Add(new EnrichedBookingCard(booking, provider, serviceType, price));
        }
        return cards;
    }

    public async Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        IReadOnlyList<NightStayBookingResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var cards = new List<EnrichedNightStayBookingCard>(bookings.Count);
        foreach (var booking in bookings)
        {
            var provider = await GetProviderAsync(
                providerCache, booking.ProviderId, booking.ServiceCategory, cancellationToken);
            // Per-night rate = the NightStay offering's rate (resolver Price).
            var (_, price) = await ResolvePriceAsync(
                booking.ProviderId, booking.ServiceId, serviceItemCode: null, cancellationToken);
            cards.Add(new EnrichedNightStayBookingCard(booking, provider, price));
        }
        return cards;
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

    private async Task<(string? ServiceType, decimal? Price)> ResolvePriceAsync(
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
                return (null, null);
            }

            // PetGroomer: price is per menu item — resolve from the booking's code.
            if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
            {
                if (string.IsNullOrWhiteSpace(serviceItemCode))
                {
                    return (offering.ServiceType, null);
                }

                var item = await offeringResolver.ResolveGroomingItemAsync(
                    providerId, serviceItemCode!, cancellationToken);
                return (offering.ServiceType,
                    item is GroomingItemResolution.Resolved resolved ? resolved.Price : null);
            }

            return (offering.ServiceType, offering.Price);
        }
        catch
        {
            // Best-effort — leave price null if the offering can't be resolved.
            return (null, null);
        }
    }
}

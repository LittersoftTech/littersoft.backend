using Pawfront.Application.Offerings;
using Pawfront.Application.Providers;
using Pawfront.Application.Reviews;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

/// <summary>
/// Enriches a pet parent's "my bookings" cards with the booked provider's
/// summary (business name / image / city), the service's price — per hour
/// for single-day services, per night for NightStay — and the parent's own review
/// state for the booking. The price PREFERS the rate
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
        Guid petParentId,
        IReadOnlyList<BookingListItemResult> bookings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        Guid petParentId,
        IReadOnlyList<NightStayBookingListItemResult> bookings,
        CancellationToken cancellationToken);
}

/// <summary>
/// The parent's own review state for one booking on their "my bookings" list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CanReview"/> here means "offer the review prompt" and is therefore
/// NOT the same predicate as the booking detail's <c>review.canReview</c>, which
/// stays true after a review exists because a review can be edited. On a list the
/// useful question is whether there is anything left to do, so this one goes false
/// once <see cref="Rating"/> is populated — the two answers together say
/// "reviewable and not yet reviewed", "already reviewed, here is the score", or
/// "not reviewable".
/// </para>
/// <para>
/// A booking is reviewable once it reaches COMPLETED or PAID — PAID sits
/// downstream of COMPLETED, so gating on COMPLETED alone would close the window
/// the moment the provider recorded payment. Custom walk-ins can never be
/// reviewed, though none appear on a parent's list (they have no PetParentId).
/// </para>
/// </remarks>
public sealed record ParentBookingReviewState(bool CanReview, int? Rating)
{
    /// <summary>A booking that is not reviewable and carries no review.</summary>
    public static ParentBookingReviewState None { get; } = new(false, null);
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
    string? ServiceDescription = null,
    ParentBookingReviewState? Review = null);

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
    BookingLocationResult Location,
    ParentBookingReviewState? Review = null);

internal sealed class ParentBookingEnrichmentService(
    IProviderDiscoveryService discovery,
    IProviderOfferingResolver offeringResolver,
    IProviderNameReader providerNameReader,
    IBookingReviewService reviewService) : IParentBookingEnrichmentService
{
    public async Task<IReadOnlyList<EnrichedBookingCard>> EnrichAsync(
        Guid petParentId,
        IReadOnlyList<BookingListItemResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var ratings = await GetRatingsAsync(petParentId, bookings.Count, cancellationToken);
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
                description,
                ResolveReview(
                    ratings,
                    ReviewedBookingTypes.SingleDay,
                    booking.BookingId,
                    booking.Status,
                    booking.Source)));
        }

        var names = await ResolveFreelancerNamesAsync(
            cards.Select(c => c.Provider), cancellationToken);
        return cards
            .Select(c => c with { Provider = FillDisplayName(c.Provider, names) })
            .ToList();
    }

    public async Task<IReadOnlyList<EnrichedNightStayBookingCard>> EnrichNightStayAsync(
        Guid petParentId,
        IReadOnlyList<NightStayBookingListItemResult> bookings,
        CancellationToken cancellationToken)
    {
        var providerCache = new Dictionary<(Guid, string), ProviderSummary?>();
        var ratings = await GetRatingsAsync(petParentId, bookings.Count, cancellationToken);
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
                item.CancellationPolicyHours, item.Location,
                // Night stays are App-only (their PetParentId is NOT NULL), so
                // there is no Custom walk-in case to exclude.
                ResolveReview(
                    ratings,
                    ReviewedBookingTypes.NightStay,
                    booking.NightStayBookingId,
                    booking.Status,
                    source: "App")));
        }

        var names = await ResolveFreelancerNamesAsync(
            cards.Select(c => c.Provider), cancellationToken);
        return cards
            .Select(c => c with { Provider = FillDisplayName(c.Provider, names) })
            .ToList();
    }

    /// <summary>
    /// The parent's own ratings, in one read for the whole page. Skipped entirely
    /// for an empty list, and best-effort like the rest of this enrichment: a
    /// failed read leaves every card unreviewable rather than failing the list.
    /// </summary>
    private async Task<IReadOnlyDictionary<(string, Guid), int>> GetRatingsAsync(
        Guid petParentId,
        int bookingCount,
        CancellationToken cancellationToken)
    {
        if (bookingCount == 0)
        {
            return new Dictionary<(string, Guid), int>();
        }

        try
        {
            var ratings = await reviewService.ListRatingsByPetParentAsync(petParentId, cancellationToken);
            return ratings.ToDictionary(r => (r.BookingType, r.BookingId), r => r.Rating);
        }
        catch
        {
            return new Dictionary<(string, Guid), int>();
        }
    }

    /// <summary>
    /// Combines "may this booking be reviewed at all" with "has it been already".
    /// The eligibility half mirrors the gate in <c>Review.UpsertBookingReview</c> —
    /// SQL remains the authority, this only decides whether to offer the prompt.
    /// </summary>
    private static ParentBookingReviewState ResolveReview(
        IReadOnlyDictionary<(string, Guid), int> ratings,
        string bookingType,
        Guid bookingId,
        string status,
        string source)
    {
        if (ratings.TryGetValue((bookingType, bookingId), out var rating))
        {
            // Already reviewed: nothing left to prompt for, and the score is what
            // the card shows instead. (The review is still editable — that route
            // is the booking detail, which reports canReview differently.)
            return new ParentBookingReviewState(CanReview: false, Rating: rating);
        }

        var isReviewable =
            string.Equals(source, "App", StringComparison.OrdinalIgnoreCase)
            && status is "COMPLETED" or "PAID";

        return isReviewable
            ? new ParentBookingReviewState(CanReview: true, Rating: null)
            : ParentBookingReviewState.None;
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

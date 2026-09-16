using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Offerings;
using Pawfront.Application.ProviderBanners;
using Pawfront.Application.ProviderServiceBanners;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Providers;

/// <summary>
/// Pipeline shared by all five searches: Cosmos discovery (category + animals +
/// city + serviceLocation + dog temperament) → provider-type check → require the
/// matching active ServiceType row → resolve the offering → per-category
/// availability probe via the shared slot service → payments filter → sort →
/// batch completed-booking counts / names / banners.
/// Pagination applies AFTER availability filtering so pages stay stable.
/// </summary>
/// <remarks>
/// The filters land at three different depths, and deliberately so — each one
/// sits as early as its data allows, because everything after the discovery call
/// costs per-provider reads. Animals / city / serviceLocation / temperament are
/// settled inside the Cosmos query; provider type is settled from the candidate's
/// sub-category before any read; payments needs a batched SQL lookup and so has
/// to wait until the match set is known.
/// </remarks>
internal sealed class ProviderSearchService(
    IProviderDiscoveryService discoveryService,
    IProviderServiceCatalog serviceCatalog,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilitySlotService slotService,
    IPetGroomerServiceRegistry petGroomerRegistry,
    IProviderBookingStatsReader bookingStatsReader,
    IProviderPayoutMethodReader payoutMethodReader,
    IProviderNameReader providerNameReader,
    IProviderServiceBannerService bannerService,
    IProviderBannerImageService providerBannerService) : IProviderSearchService
{
    // 1-minute granularity when the parent's exact window must be matched
    // (day care); 15 minutes when ANY free slot on the date is enough.
    private const int ExactWindowGranularityMinutes = 1;
    private const int AnySlotGranularityMinutes = 15;

    public Task<IReadOnlyList<ProviderSearchResult>> SearchDayCareAsync(
        DayCareProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetSitter),
            ProviderServiceTypes.DayCare,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take, criteria.Refinements, criteria.PetTemperament,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.Date is not null)
                {
                    // The parent needs care over the WHOLE window, so probe a
                    // booking of exactly the window's length: it must be at
                    // least the offering minimum and start exactly on time.
                    var windowHours = (decimal)(criteria.EndTime!.Value - criteria.StartTime!.Value).TotalHours;
                    if (windowHours < resolved.DurationHours)
                    {
                        return null;
                    }

                    var slots = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.Date.Value,
                        windowHours, ExactWindowGranularityMinutes, serviceItemCode: null, ct);
                    if (slots is null || !AnySlotInsideWindow(slots.Slots, criteria.StartTime.Value, criteria.EndTime.Value))
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerHour,
                    ServiceItemCode: null, summary.ImageUrl,
                    PetCapacity: resolved.Capacity);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchNightStayAsync(
        NightStayProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetSitter),
            ProviderServiceTypes.NightStay,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take, criteria.Refinements, criteria.PetTemperament,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.StartDate is not null)
                {
                    // Every stayed night needs free capacity; the pickup date
                    // itself is checkout and not checked. NightStay availability
                    // is date-granular — one per-night query covers the whole
                    // range (closures + occupancy vs the per-night capacity).
                    var lastNight = criteria.PickupDate!.Value.AddDays(-1);
                    var nightResult = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.StartDate.Value,
                        durationHours: 0m, AnySlotGranularityMinutes, serviceItemCode: null, ct,
                        endDate: lastNight);
                    if (nightResult?.Nights is null || nightResult.Nights.Any(n => !n.IsAvailable))
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerHour,
                    ServiceItemCode: null, summary.ImageUrl,
                    PetCapacity: resolved.Capacity);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchGroomingAsync(
        GroomingProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetGroomer),
            ProviderServiceTypes.GroomingSession,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take, criteria.Refinements, criteria.PetTemperament,
            async (summary, service, resolved, ct) =>
            {
                decimal? charges = null;
                string? probeCode;
                // Only a code-scoped search describes one item; a browse across
                // the whole menu has no single blurb to show.
                string? description = null;

                if (criteria.ServiceItemCode is not null)
                {
                    // Only providers with the requested item ACTIVE on their
                    // menu match; their per-item price becomes the charge.
                    if (await offeringResolver.ResolveGroomingItemAsync(
                            resolved.ProviderId, criteria.ServiceItemCode, ct)
                        is not GroomingItemResolution.Resolved item)
                    {
                        return null;
                    }
                    charges = item.Price;
                    probeCode = item.Code;
                    description = item.Description;
                }
                else
                {
                    // No specific item requested — the groomer just needs at
                    // least one active menu item. The shortest one is probed
                    // for availability: grooming capacity is shop-wide, so if
                    // the shortest item has no free slot, no item does.
                    var groomer = await petGroomerRegistry.GetAsync(resolved.ProviderId, ct);
                    var shortest = (groomer?.GroomerShop?.Offering?.Session?.Services
                            ?? groomer?.Freelance?.Offering?.Session?.Services)
                        ?.Where(i => i.IsActive)
                        .OrderBy(i => i.DurationMinutes)
                        .FirstOrDefault();
                    if (shortest is null)
                    {
                        return null;
                    }
                    probeCode = shortest.Code;
                }

                if (criteria.Date is not null)
                {
                    var slots = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.Date.Value,
                        durationHours: 0m, AnySlotGranularityMinutes, probeCode, ct);
                    if (slots is null || !slots.Slots.Any(s => s.RemainingCapacity > 0))
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, charges, ProviderSearchChargesUnits.PerService,
                    criteria.ServiceItemCode, summary.ImageUrl, description,
                    PetCapacity: resolved.Capacity);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchVetAsync(
        VetProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.Vet),
            ProviderServiceTypes.VetAppointment,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take, criteria.Refinements, criteria.PetTemperament,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.Date is not null)
                {
                    var slots = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.Date.Value,
                        resolved.DurationHours, AnySlotGranularityMinutes, serviceItemCode: null, ct);
                    if (slots is null || !slots.Slots.Any(s => s.RemainingCapacity > 0))
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerAppointment,
                    ServiceItemCode: null, summary.ImageUrl,
                    PetCapacity: resolved.Capacity);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchTrainerAsync(
        TrainerProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetTrainer),
            ProviderServiceTypes.TrainingSession,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take, criteria.Refinements, criteria.PetTemperament,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.Date is not null)
                {
                    // A training session is fixed-duration — any free slot of
                    // that length on the date means the provider is bookable.
                    var slots = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.Date.Value,
                        resolved.DurationHours, AnySlotGranularityMinutes, serviceItemCode: null, ct);
                    if (slots is null || !slots.Slots.Any(s => s.RemainingCapacity > 0))
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerSession,
                    ServiceItemCode: null, summary.ImageUrl, resolved.Description,
                    PetCapacity: resolved.Capacity);
            },
            cancellationToken);

    private async Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(
        string serviceCategory,
        string serviceType,
        IReadOnlyCollection<string>? animals,
        string? city,
        string? serviceLocation,
        int skip,
        int take,
        ProviderSearchRefinements? refinements,
        // The temperament of the pet the parent is shopping for, when they named
        // one. A HINT stamped onto every card, never a filter — see
        // PetTemperamentMatch.
        string? petTemperament,
        Func<ProviderSummary, ProviderService, OfferingResolution.Resolved, CancellationToken, Task<ProviderSearchResult?>> evaluateAsync,
        CancellationToken cancellationToken)
    {
        var providerType = refinements?.ProviderType;
        var paymentMethods = refinements?.PaymentMethods;
        var sortBy = refinements?.SortBy;

        var candidates = await discoveryService.ListAsync(
            new ProviderDiscoveryFilter(
                serviceCategory, animals, city, serviceLocation, Skip: 0, Take: int.MaxValue,
                refinements?.DogTemperaments),
            cancellationToken);

        // The early exit below is what keeps an unsorted search from probing
        // availability for every provider in the category. It only survives when
        // the page can be taken from a PREFIX of the candidates — a sort needs the
        // whole set ordered first, and the payments filter is decided by a batch
        // read that happens after the loop, so either one forces a full pass.
        var evaluateAll = sortBy is not null || paymentMethods is { Count: > 0 };
        var needed = evaluateAll ? int.MaxValue : skip + take;

        var matches = new List<ProviderSearchResult>();
        foreach (var candidate in candidates)
        {
            // Business-vs-freelancer is carried by the sub-category, so it is
            // settled here — before the catalog lookup, the offering resolve and
            // the availability probe, which are the expensive steps.
            if (!ProviderTypeFilters.Matches(providerType, candidate.SubCategory))
            {
                continue;
            }

            var services = await serviceCatalog.ListByProviderAsync(
                candidate.ProviderId, includeInactive: false, cancellationToken);
            var service = services.FirstOrDefault(
                s => string.Equals(s.ServiceType, serviceType, StringComparison.Ordinal));
            if (service is null)
            {
                continue;
            }

            if (await offeringResolver.ResolveAsync(service.ServiceId, cancellationToken)
                is not OfferingResolution.Resolved resolved)
            {
                continue;
            }

            var result = await evaluateAsync(candidate, service, resolved, cancellationToken);
            if (result is null)
            {
                continue;
            }

            // Stamped here rather than inside each evaluate callback: the answer
            // depends only on the candidate and the pet, so one place is one
            // place the five searches can agree.
            matches.Add(result with
            {
                MatchesPetTemperament =
                    PetTemperamentMatch.Evaluate(petTemperament, candidate.DogTemperaments)
            });
            if (matches.Count >= needed)
            {
                break;
            }
        }

        // "Does this provider take my money?" — one batched SQL read, because the
        // accepted methods live in SQL while discovery lists from Cosmos. A
        // provider with no saved payout policy is absent from the map and is
        // excluded: the parent asked who takes Cash, and "not recorded" is not a
        // yes. ALL of the requested methods must be accepted.
        if (paymentMethods is { Count: > 0 } && matches.Count > 0)
        {
            var accepted = await payoutMethodReader.GetPayoutMethodsAsync(
                matches.Select(m => m.ProviderId).Distinct().ToArray(), cancellationToken);
            matches = matches
                .Where(m => accepted.TryGetValue(m.ProviderId, out var offered)
                            && paymentMethods.All(offered.Contains))
                .ToList();
        }

        // The completed-booking count is both a card field and a sort key, so it
        // is fetched once for whichever set needs it: the whole match list when it
        // decides the order, the page alone otherwise.
        IReadOnlyDictionary<Guid, int>? counts = null;
        if (sortBy == ProviderSearchSortBy.Bookings && matches.Count > 0)
        {
            counts = await bookingStatsReader.GetCompletedBookingCountsAsync(
                matches.Select(m => m.ProviderId).Distinct().ToArray(), cancellationToken);
            matches = matches
                .Select(m => counts.TryGetValue(m.ProviderId, out var c)
                    ? m with { CompletedBookings = c }
                    : m)
                .ToList();
        }

        if (sortBy is not null)
        {
            matches = Sort(matches, sortBy.Value, refinements!.SortDirection);
        }

        var paged = matches.Skip(skip).Take(take).ToList();
        if (paged.Count == 0)
        {
            return paged;
        }

        var providerIds = paged.Select(r => r.ProviderId).Distinct().ToArray();

        counts ??= await bookingStatsReader.GetCompletedBookingCountsAsync(
            providerIds, cancellationToken);

        // Freelancers have no business name in the Cosmos offering doc — fall
        // back to the provider's personal name so the card always has a label.
        var names = await providerNameReader.GetProviderDisplayNamesAsync(
            providerIds, cancellationToken);

        // Per-service banner (the wide card image the provider uploaded for this
        // ServiceId). Absent from the map = the provider hasn't set one, in
        // which case the card falls back to the provider-level banner captured
        // at registration — the picture most providers actually have.
        var serviceIds = paged.Select(r => r.ServiceId).Distinct().ToArray();
        var banners = await bannerService.GetByServiceIdsAsync(serviceIds, cancellationToken);
        var providerBanners = await providerBannerService.GetByProviderIdsAsync(
            providerIds, cancellationToken);

        return paged
            .Select(r =>
            {
                var businessName = r.BusinessName;
                if (string.IsNullOrWhiteSpace(businessName)
                    && names.TryGetValue(r.ProviderId, out var personName))
                {
                    businessName = personName;
                }

                var completed = counts.TryGetValue(r.ProviderId, out var count)
                    ? count
                    : r.CompletedBookings;

                var bannerImageUrl = banners.TryGetValue(r.ServiceId, out var banner)
                    ? banner
                    : providerBanners.TryGetValue(r.ProviderId, out var providerBanner)
                        ? providerBanner
                        : null;

                return r with
                {
                    BusinessName = businessName,
                    CompletedBookings = completed,
                    BannerImageUrl = bannerImageUrl
                };
            })
            .ToList();
    }

    /// <summary>
    /// Orders the whole match set before it is paged. In memory rather than in a
    /// query because the three keys come from three different places — the price
    /// and the capacity from the Cosmos offering, the booking count from SQL —
    /// so there is no one store that could do it.
    /// </summary>
    private static List<ProviderSearchResult> Sort(
        List<ProviderSearchResult> matches,
        ProviderSearchSortBy sortBy,
        Earnings.EarningsSortDirection direction)
    {
        var ascending = direction == Earnings.EarningsSortDirection.Ascending;

        IOrderedEnumerable<ProviderSearchResult> ordered = sortBy switch
        {
            // A hit with no price — a groomers browse with no serviceItemCode, or
            // an offering that records none — has nothing to compare, so it sorts
            // LAST in both directions rather than pretending to cost zero.
            ProviderSearchSortBy.Price => ascending
                ? matches.OrderBy(m => m.Charges is null).ThenBy(m => m.Charges)
                : matches.OrderBy(m => m.Charges is null).ThenByDescending(m => m.Charges),
            ProviderSearchSortBy.Bookings => ascending
                ? matches.OrderBy(m => m.CompletedBookings)
                : matches.OrderByDescending(m => m.CompletedBookings),
            _ => ascending
                ? matches.OrderBy(m => m.PetCapacity)
                : matches.OrderByDescending(m => m.PetCapacity)
        };

        // Deterministic tie-break. Without it two providers sharing a price (or a
        // capacity — most of them will) can come back in a different order on the
        // next call, which makes skip/take paging repeat or skip a card.
        return ordered.ThenBy(m => m.ProviderId).ThenBy(m => m.ServiceId).ToList();
    }

    private async Task<AvailableSlotsResult?> TryGetSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        string? serviceItemCode,
        CancellationToken cancellationToken,
        DateOnly? endDate = null)
    {
        try
        {
            return await slotService.GetAvailableSlotsAsync(
                providerId, serviceId, date, durationHours, granularityMinutes,
                serviceItemCode, cancellationToken, endDate);
        }
        catch (Exception ex) when (
            ex is SlotServiceInvalidException
                or ProviderServiceNotRegisteredException
                or ProviderOfferingNotConfiguredException
                or InvalidBookingDurationException
                or SlotGroomingItemCodeRequiredException
                or SlotGroomingItemNotOfferedException
                or SlotGroomingItemInactiveException)
        {
            // A service the slot walker can't compute is simply not bookable —
            // search results must not surface per-provider config errors.
            return null;
        }
    }

    private static bool AnySlotInsideWindow(
        IReadOnlyCollection<TimeSlot> slots,
        TimeOnly startTime,
        TimeOnly endTime)
    {
        foreach (var slot in slots)
        {
            // Zero-capacity slots are emitted for display but are not bookable.
            if (slot.RemainingCapacity > 0 && slot.StartTime >= startTime && slot.EndTime <= endTime)
            {
                return true;
            }
        }
        return false;
    }
}

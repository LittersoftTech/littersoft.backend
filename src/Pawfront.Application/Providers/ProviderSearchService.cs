using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Offerings;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Providers;

/// <summary>
/// Pipeline shared by all four searches: Cosmos discovery (category +
/// animals + city + serviceLocation) → require the matching active
/// ServiceType row → resolve the offering → per-category availability
/// probe via the shared slot service → batch completed-booking counts.
/// Pagination applies AFTER availability filtering so pages stay stable.
/// </summary>
internal sealed class ProviderSearchService(
    IProviderDiscoveryService discoveryService,
    IProviderServiceCatalog serviceCatalog,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilitySlotService slotService,
    IPetGroomerServiceRegistry petGroomerRegistry,
    IProviderBookingStatsReader bookingStatsReader) : IProviderSearchService
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
            criteria.Skip, criteria.Take,
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
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerHour, ServiceItemCode: null);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchNightStayAsync(
        NightStayProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetSitter),
            ProviderServiceTypes.NightStay,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.StartDate is not null)
                {
                    // Every stayed night needs free capacity; the pickup date
                    // itself is checkout and not checked. Probe the offering's
                    // minimum duration — any free slot on the date means the
                    // night-stay bucket still has room.
                    for (var night = criteria.StartDate.Value; night < criteria.PickupDate!.Value; night = night.AddDays(1))
                    {
                        var slots = await TryGetSlotsAsync(
                            resolved.ProviderId, service.ServiceId, night,
                            resolved.DurationHours, AnySlotGranularityMinutes, serviceItemCode: null, ct);
                        if (slots is null || slots.Slots.Count == 0)
                        {
                            return null;
                        }
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerHour, ServiceItemCode: null);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchGroomingAsync(
        GroomingProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.PetGroomer),
            ProviderServiceTypes.GroomingSession,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take,
            async (summary, service, resolved, ct) =>
            {
                decimal? charges = null;
                string? probeCode;

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
                    if (slots is null || slots.Slots.Count == 0)
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, charges, ProviderSearchChargesUnits.PerService, criteria.ServiceItemCode);
            },
            cancellationToken);

    public Task<IReadOnlyList<ProviderSearchResult>> SearchVetAsync(
        VetProviderSearchCriteria criteria,
        CancellationToken cancellationToken) =>
        SearchAsync(
            nameof(ProviderServiceCategory.Vet),
            ProviderServiceTypes.VetAppointment,
            criteria.Animals, criteria.City, criteria.ServiceLocation,
            criteria.Skip, criteria.Take,
            async (summary, service, resolved, ct) =>
            {
                if (criteria.Date is not null)
                {
                    var slots = await TryGetSlotsAsync(
                        resolved.ProviderId, service.ServiceId, criteria.Date.Value,
                        resolved.DurationHours, AnySlotGranularityMinutes, serviceItemCode: null, ct);
                    if (slots is null || slots.Slots.Count == 0)
                    {
                        return null;
                    }
                }

                return new ProviderSearchResult(
                    summary.ProviderId, service.ServiceId, service.SubCategory, summary.DisplayName,
                    CompletedBookings: 0, resolved.Price, ProviderSearchChargesUnits.PerAppointment, ServiceItemCode: null);
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
        Func<ProviderSummary, ProviderService, OfferingResolution.Resolved, CancellationToken, Task<ProviderSearchResult?>> evaluateAsync,
        CancellationToken cancellationToken)
    {
        var candidates = await discoveryService.ListAsync(
            new ProviderDiscoveryFilter(
                serviceCategory, animals, city, serviceLocation, Skip: 0, Take: int.MaxValue),
            cancellationToken);

        var needed = skip + take;
        var matches = new List<ProviderSearchResult>();
        foreach (var candidate in candidates)
        {
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

            matches.Add(result);
            if (matches.Count >= needed)
            {
                break;
            }
        }

        var paged = matches.Skip(skip).ToList();
        if (paged.Count == 0)
        {
            return paged;
        }

        var counts = await bookingStatsReader.GetCompletedBookingCountsAsync(
            paged.Select(r => r.ProviderId).Distinct().ToArray(), cancellationToken);

        return paged
            .Select(r => counts.TryGetValue(r.ProviderId, out var completed)
                ? r with { CompletedBookings = completed }
                : r)
            .ToList();
    }

    private async Task<AvailableSlotsResult?> TryGetSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        string? serviceItemCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await slotService.GetAvailableSlotsAsync(
                providerId, serviceId, date, durationHours, granularityMinutes,
                serviceItemCode, cancellationToken);
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
            if (slot.StartTime >= startTime && slot.EndTime <= endTime)
            {
                return true;
            }
        }
        return false;
    }
}

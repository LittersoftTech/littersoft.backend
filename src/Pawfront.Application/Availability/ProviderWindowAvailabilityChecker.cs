using Pawfront.Application.Offerings;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Availability;

/// <summary>
/// Composes the per-service catalog, the offering resolver, and the existing
/// slot service to decide whether a provider can take a booking in the
/// requested window. All capacity / closure / booking subtraction is
/// delegated to <see cref="IProviderAvailabilitySlotService"/> so this stays
/// in lock-step with what the slots endpoint reports.
/// </summary>
internal sealed class ProviderWindowAvailabilityChecker(
    IProviderServiceCatalog serviceCatalog,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilitySlotService slotService,
    IPetGroomerServiceRegistry petGroomerRegistry) : IProviderWindowAvailabilityChecker
{
    // 1-minute probe granularity so slots align with the parent's exact
    // start time regardless of when the provider's working day begins. The
    // walk is in-memory over a single day — at most ~1.4k iterations.
    private const int ProbeGranularityMinutes = 1;

    public async Task<bool> HasBookableWindowAsync(
        Guid providerId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var windowHours = (decimal)(endTime - startTime).TotalHours;
        if (windowHours <= 0)
        {
            return false;
        }

        var services = await serviceCatalog.ListByProviderAsync(
            providerId, includeInactive: false, cancellationToken);

        foreach (var service in services)
        {
            var available = service.ServiceType == ProviderServiceTypes.GroomingSession
                ? await HasGroomingSlotAsync(providerId, service.ServiceId, date, startTime, endTime, cancellationToken)
                : await HasStandardSlotAsync(providerId, service.ServiceId, date, startTime, endTime, windowHours, cancellationToken);

            if (available)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasStandardSlotAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        decimal windowHours,
        CancellationToken cancellationToken)
    {
        if (await offeringResolver.ResolveAsync(serviceId, cancellationToken)
            is not OfferingResolution.Resolved offering)
        {
            return false;
        }

        // Window shorter than the service's fixed/minimum duration can never
        // host a booking — skip without hitting the slot walker.
        if (windowHours < offering.DurationHours)
        {
            return false;
        }

        // Fixed duration (TrainingSession / VetAppointment): probe at the
        // fixed length and accept any free slot inside the window.
        // Minimum duration (DayCare / NightStay): the parent wants care over
        // the whole window, so probe the full window length — only the slot
        // starting exactly at startTime can satisfy it.
        var probeHours = offering.IsDurationFixed ? offering.DurationHours : windowHours;

        var result = await TryGetSlotsAsync(
            providerId, serviceId, date, probeHours, serviceItemCode: null, cancellationToken);

        return result is not null && AnySlotInsideWindow(result.Slots, startTime, endTime);
    }

    private async Task<bool> HasGroomingSlotAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        // Grooming durations are per menu item and capacity is shop-wide, so
        // probing the SHORTEST active item that fits the window is
        // sufficient: if even that one has no free slot inside the window,
        // no longer item can have one either.
        var groomer = await petGroomerRegistry.GetAsync(providerId, cancellationToken);
        var items = groomer?.GroomerShop?.Offering?.Session?.Services
            ?? groomer?.Freelance?.Offering?.Session?.Services;
        if (items is null)
        {
            return false;
        }

        var windowMinutes = (endTime - startTime).TotalMinutes;
        var shortest = items
            .Where(i => i.IsActive && i.DurationMinutes <= windowMinutes)
            .OrderBy(i => i.DurationMinutes)
            .FirstOrDefault();
        if (shortest is null)
        {
            return false;
        }

        var result = await TryGetSlotsAsync(
            providerId, serviceId, date, durationHours: 0m, shortest.Code, cancellationToken);

        return result is not null && AnySlotInsideWindow(result.Slots, startTime, endTime);
    }

    private async Task<AvailableSlotsResult?> TryGetSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        string? serviceItemCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await slotService.GetAvailableSlotsAsync(
                providerId,
                serviceId,
                date,
                durationHours,
                ProbeGranularityMinutes,
                serviceItemCode,
                cancellationToken);
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
            // A service we can't compute slots for is simply not bookable in
            // this window — discovery filtering must not surface per-provider
            // configuration errors to the searching parent.
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

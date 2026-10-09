using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Availability;

internal sealed class ProviderAvailabilitySlotService(
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IDailyBookingReader bookingReader,
    INightStayOccupancyReader nightStayOccupancyReader,
    IProviderClosureReader closureReader) : IProviderAvailabilitySlotService
{
    private const int MinGranularityMinutes = 1;
    private const int MaxGranularityMinutes = 240;

    // A night-availability query can span at most this many nights (matches the
    // 30-night stay cap, +1 headroom for a checkout-day-inclusive calendar).
    private const int MaxNightRangeNights = 31;

    public async Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        string? serviceItemCode,
        CancellationToken cancellationToken,
        DateOnly? endDate = null)
    {
        if (granularityMinutes is < MinGranularityMinutes or > MaxGranularityMinutes)
        {
            throw new ArgumentException(
                $"granularityMinutes must be between {MinGranularityMinutes} and {MaxGranularityMinutes}.",
                nameof(granularityMinutes));
        }

        // 1. Resolve the specific service: capacity + duration rule.
        var resolution = await offeringResolver.ResolveAsync(serviceId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotFound => throw new SlotServiceInvalidException(serviceId, providerId),
            OfferingResolution.Inactive => throw new SlotServiceInvalidException(serviceId, providerId),
            OfferingResolution.NotConfigured nc => throw new ProviderOfferingNotConfiguredException(nc.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r when r.ProviderId != providerId
                => throw new SlotServiceInvalidException(serviceId, providerId),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        // NightStay uses nightly capacity, while respecting weekly days off and time off.
        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            return await GetNightAvailabilityAsync(providerId, serviceId, offering, date, endDate, cancellationToken);
        }

        // 1b. PetGroomer: ignore the request's durationHours; resolve the menu
        // item's duration server-side. The code is REQUIRED for this category.
        if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
        {
            if (string.IsNullOrWhiteSpace(serviceItemCode))
            {
                throw new SlotGroomingItemCodeRequiredException();
            }

            var itemResolution = await offeringResolver.ResolveGroomingItemAsync(
                providerId, serviceItemCode.Trim(), cancellationToken);

            offering = itemResolution switch
            {
                GroomingItemResolution.OfferingMissing
                    => throw new ProviderOfferingNotConfiguredException(providerId, offering.ServiceCategory),
                GroomingItemResolution.NotOffered no
                    => throw new SlotGroomingItemNotOfferedException(providerId, no.Code),
                GroomingItemResolution.Inactive ia
                    => throw new SlotGroomingItemInactiveException(providerId, ia.Code),
                GroomingItemResolution.Resolved ri => offering with
                {
                    DurationHours = (decimal)ri.DurationMinutes / 60m,
                    IsDurationFixed = true
                },
                _ => throw new InvalidOperationException("Unknown grooming item resolution.")
            };

            durationHours = offering.DurationHours;
        }
        else if (durationHours <= 0)
        {
            // Non-grooming categories still require an explicit durationHours.
            throw new ArgumentException("durationHours must be greater than zero.", nameof(durationHours));
        }

        // 2. Validate the requested duration against the offering's duration rule.
        ValidateDuration(durationHours, offering);

        // 3. Read the weekly schedule and find this date's weekday row.
        var weekly = await availabilityService.GetAsync(providerId, cancellationToken);
        var windows = ProviderWorkingHours.Windows(weekly, date);
        if (windows.Count == 0)
            return EmptySlots(providerId, serviceId, offering, date, durationHours, granularityMinutes);

        // Any service time off on the requested date makes it unavailable.
        var closures = await closureReader.GetActiveClosuresForDateAsync(serviceId, date, cancellationToken);
        if (closures.Count > 0)
        {
            return EmptySlots(providerId, serviceId, offering, date, durationHours, granularityMinutes);
        }

        // 4. Split working hours around the provider's regular break.
        // Windows already exclude local breaks and are projected to this UTC date.

        // 5. Read confirmed bookings for this service+date so we can subtract overlapping slots.
        var existingBookings = await bookingReader.GetBookingsForDateAsync(serviceId, date, cancellationToken);

        // 6. Walk each window, emit every slot with its remaining capacity
        //    (fully-booked slots included, at remainingCapacity 0).
        var durationSpan = TimeSpan.FromHours((double)durationHours);
        var step = TimeSpan.FromMinutes(granularityMinutes);

        // A booking needs a minimum lead time, so a slot starting sooner than
        // that isn't bookable and is left out entirely — browsing at 11:00 makes
        // 13:00 the first slot offered. Dropping (rather than zeroing) these is
        // deliberate: remainingCapacity 0 means "full, try another day", which is
        // the wrong thing to tell someone about a slot that simply came too soon.
        // This is also what keeps today's ALREADY-ELAPSED slots out of the list.
        var earliestStart = BookingLeadTime.EarliestBookableStart(DateTimeOffset.UtcNow);

        var slots = new List<TimeSlot>();
        foreach (var (windowStart, windowEnd) in windows)
        {
            var cursorTicks = windowStart.Ticks;
            var endTicksLimit = windowEnd.Ticks - durationSpan.Ticks;

            while (cursorTicks <= endTicksLimit)
            {
                var slotStart = new TimeOnly(cursorTicks);
                var slotEnd = slotStart.Add(durationSpan);

                if (BookingLeadTime.ServiceStartUtc(date, slotStart) >= earliestStart)
                {
                    // Same overlap count the race-safe create sproc uses, so the
                    // remaining capacity shown here is exactly what create will admit.
                    // Fully-booked slots are still emitted with remainingCapacity 0
                    // so the client can render them as unavailable.
                    var overlap = CountOverlaps(existingBookings, slotStart, slotEnd);
                    slots.Add(new TimeSlot(slotStart, slotEnd, Math.Max(0, offering.Capacity - overlap)));
                }

                cursorTicks += step.Ticks;
            }
        }

        return new AvailableSlotsResult(
            providerId,
            serviceId,
            date,
            offering.ServiceCategory,
            offering.SubCategory,
            offering.ServiceType,
            durationHours,
            offering.Capacity,
            granularityMinutes,
            slots);
    }

    /// <summary>
    /// Date-granular NightStay availability: one entry per night in
    /// [<paramref name="fromNight"/>, <paramref name="toNight"/>] with the
    /// night's active-stay count and remaining capacity. A night is unavailable
    /// when a weekly day off or service time off covers it, capacity is exhausted,
    /// or the stay would begin inside the booking lead-time window.
    /// </summary>
    private async Task<AvailableSlotsResult> GetNightAvailabilityAsync(
        Guid providerId,
        Guid serviceId,
        OfferingResolution.Resolved offering,
        DateOnly fromNight,
        DateOnly? toNight,
        CancellationToken cancellationToken)
    {
        var lastNight = toNight ?? fromNight;
        if (lastNight < fromNight)
        {
            throw new ArgumentException("endDate must be on or after date.", nameof(toNight));
        }
        if (lastNight.DayNumber - fromNight.DayNumber + 1 > MaxNightRangeNights)
        {
            throw new ArgumentException(
                $"A night-availability query can span at most {MaxNightRangeNights} nights.",
                nameof(toNight));
        }

        var occupancy = await nightStayOccupancyReader.GetNightlyOccupancyAsync(
            serviceId, fromNight, lastNight, cancellationToken);

        // A stay begins at drop-off on its check-in night, so the same lead-time
        // rule the hourly walk applies gates the earliest bookable night. The
        // night is still RETURNED (its occupancy is real information for a
        // calendar) — it just isn't available. A pre-2026-08 offering with no
        // drop-off time recorded falls back to the start of the night.
        var earliestStart = BookingLeadTime.EarliestBookableStart(DateTimeOffset.UtcNow);
        var dropOff = offering.DropOffTime ?? TimeOnly.MinValue;

        var weekly = await availabilityService.GetAsync(providerId, cancellationToken);
        var nights = new List<NightAvailability>();
        for (var night = fromNight; night <= lastNight; night = night.AddDays(1))
        {
            var closures = await closureReader.GetActiveClosuresForDateAsync(serviceId, night, cancellationToken);
            var isClosed = closures.Count > 0
                || !weekly.Days.Any(d => d.DayOfWeek == (int)night.DayOfWeek && d.IsOpen);
            var isTooSoon = BookingLeadTime.ServiceStartUtc(night, dropOff) < earliestStart;

            var activeStays = occupancy.TryGetValue(night, out var count) ? count : 0;
            var remaining = isClosed ? 0 : Math.Max(0, offering.Capacity - activeStays);

            nights.Add(new NightAvailability(
                night,
                activeStays,
                remaining,
                isClosed,
                IsAvailable: remaining > 0 && !isTooSoon));
        }

        return new AvailableSlotsResult(
            providerId,
            serviceId,
            fromNight,
            offering.ServiceCategory,
            offering.SubCategory,
            offering.ServiceType,
            DurationHours: 0m,
            offering.Capacity,
            GranularityMinutes: 0,
            Slots: Array.Empty<TimeSlot>(),
            Nights: nights);
    }

    private static AvailableSlotsResult EmptySlots(
        Guid providerId,
        Guid serviceId,
        OfferingResolution.Resolved offering,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes) =>
        new(providerId,
            serviceId,
            date,
            offering.ServiceCategory,
            offering.SubCategory,
            offering.ServiceType,
            durationHours,
            offering.Capacity,
            granularityMinutes,
            Array.Empty<TimeSlot>());

    private static int CountOverlaps(
        IReadOnlyList<BookingWindow> bookings,
        TimeOnly slotStart,
        TimeOnly slotEnd)
    {
        var count = 0;
        foreach (var booking in bookings)
        {
            // Standard half-open overlap: a slot [s, e) overlaps a booking [bs, be) if s < be && e > bs.
            if (booking.StartTime < slotEnd && booking.EndTime > slotStart)
            {
                count++;
            }
        }
        return count;
    }

    private static void ValidateDuration(decimal durationHours, OfferingResolution.Resolved offering)
    {
        if (offering.IsDurationFixed && durationHours != offering.DurationHours)
        {
            throw new InvalidBookingDurationException(
                $"This service requires a fixed booking duration of {offering.DurationHours} hours.");
        }

        if (!offering.IsDurationFixed && durationHours < offering.DurationHours)
        {
            throw new InvalidBookingDurationException(
                $"This service requires a minimum booking duration of {offering.DurationHours} hours.");
        }
    }
}

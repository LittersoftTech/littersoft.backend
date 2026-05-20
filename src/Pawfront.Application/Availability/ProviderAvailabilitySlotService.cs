using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;

namespace Pawfront.Application.Availability;

internal sealed class ProviderAvailabilitySlotService(
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IDailyBookingReader bookingReader,
    IProviderClosureReader closureReader) : IProviderAvailabilitySlotService
{
    private const int MinGranularityMinutes = 1;
    private const int MaxGranularityMinutes = 240;

    public async Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        CancellationToken cancellationToken)
    {
        if (durationHours <= 0)
        {
            throw new ArgumentException("durationHours must be greater than zero.", nameof(durationHours));
        }

        if (granularityMinutes is < MinGranularityMinutes or > MaxGranularityMinutes)
        {
            throw new ArgumentException(
                $"granularityMinutes must be between {MinGranularityMinutes} and {MaxGranularityMinutes}.",
                nameof(granularityMinutes));
        }

        // 1. Resolve the provider's single service registration + offering.
        var resolution = await offeringResolver.ResolveAsync(providerId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotRegistered => throw new ProviderServiceNotRegisteredException(providerId),
            OfferingResolution.NotConfigured nc => throw new ProviderOfferingNotConfiguredException(providerId, nc.ServiceCategory),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        // 2. Validate the requested duration against the offering's duration rule.
        ValidateDuration(durationHours, offering);

        // 3. Read the weekly schedule and find this date's weekday row.
        var weekly = await availabilityService.GetAsync(providerId, cancellationToken);
        var dayOfWeek = (int)date.DayOfWeek;
        var daySchedule = weekly.Days.FirstOrDefault(d => d.DayOfWeek == dayOfWeek);

        if (daySchedule is null
            || !daySchedule.IsOpen
            || daySchedule.StartTime is null
            || daySchedule.EndTime is null)
        {
            return new AvailableSlotsResult(
                providerId,
                date,
                offering.ServiceCategory,
                offering.SubCategory,
                durationHours,
                offering.Capacity,
                granularityMinutes,
                Array.Empty<TimeSlot>());
        }

        // 3b. Apply any ad-hoc closures (sick leave / vacation). A full-day closure
        //     short-circuits to zero slots; partial-day closures are treated as
        //     extra breaks that carve the working windows further.
        var closures = await closureReader.GetActiveClosuresForDateAsync(providerId, date, cancellationToken);
        if (closures.Any(c => c.IsFullDay))
        {
            return new AvailableSlotsResult(
                providerId,
                date,
                offering.ServiceCategory,
                offering.SubCategory,
                durationHours,
                offering.Capacity,
                granularityMinutes,
                Array.Empty<TimeSlot>());
        }

        // 4. Build the working windows (split around break if set, then split
        //    again around any partial-day closure windows).
        var windows = BuildWorkingWindows(daySchedule);
        windows = SubtractClosureWindows(windows, closures);

        // 5. Read confirmed bookings for this date so we can subtract overlapping slots.
        var existingBookings = await bookingReader.GetBookingsForDateAsync(providerId, date, cancellationToken);

        // 6. Walk each window, emit slots whose overlap count is below capacity.
        var durationSpan = TimeSpan.FromHours((double)durationHours);
        var step = TimeSpan.FromMinutes(granularityMinutes);

        var slots = new List<TimeSlot>();
        foreach (var (windowStart, windowEnd) in windows)
        {
            var cursorTicks = windowStart.Ticks;
            var endTicksLimit = windowEnd.Ticks - durationSpan.Ticks;

            while (cursorTicks <= endTicksLimit)
            {
                var slotStart = new TimeOnly(cursorTicks);
                var slotEnd = slotStart.Add(durationSpan);

                var overlap = CountOverlaps(existingBookings, slotStart, slotEnd);
                if (overlap < offering.Capacity)
                {
                    slots.Add(new TimeSlot(slotStart, slotEnd));
                }

                cursorTicks += step.Ticks;
            }
        }

        return new AvailableSlotsResult(
            providerId,
            date,
            offering.ServiceCategory,
            offering.SubCategory,
            durationHours,
            offering.Capacity,
            granularityMinutes,
            slots);
    }

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

    /// <summary>
    /// Removes each partial-day closure window from the working windows. A closure
    /// that lies fully inside a window splits it in two; one that overlaps an edge
    /// trims the edge; one that covers a window removes it entirely.
    /// </summary>
    private static List<(TimeOnly Start, TimeOnly End)> SubtractClosureWindows(
        List<(TimeOnly Start, TimeOnly End)> windows,
        IReadOnlyList<ActiveClosure> closures)
    {
        if (closures.Count == 0)
        {
            return windows;
        }

        var current = windows;
        foreach (var closure in closures)
        {
            // Full-day closures are already handled upstream — defensive skip.
            if (closure.StartTime is null || closure.EndTime is null) continue;

            var cs = closure.StartTime.Value;
            var ce = closure.EndTime.Value;
            var next = new List<(TimeOnly Start, TimeOnly End)>(current.Count);

            foreach (var (ws, we) in current)
            {
                // No overlap → keep window unchanged.
                if (ce <= ws || cs >= we)
                {
                    next.Add((ws, we));
                    continue;
                }

                // Closure covers the whole window → drop it.
                if (cs <= ws && ce >= we)
                {
                    continue;
                }

                // Left fragment.
                if (cs > ws)
                {
                    next.Add((ws, cs));
                }

                // Right fragment.
                if (ce < we)
                {
                    next.Add((ce, we));
                }
            }

            current = next;
        }

        return current;
    }

    private static List<(TimeOnly Start, TimeOnly End)> BuildWorkingWindows(DayAvailabilityResult day)
    {
        var windows = new List<(TimeOnly, TimeOnly)>();

        if (day.BreakStartTime is null || day.BreakEndTime is null)
        {
            windows.Add((day.StartTime!.Value, day.EndTime!.Value));
        }
        else
        {
            var firstWindow = (day.StartTime!.Value, day.BreakStartTime.Value);
            var secondWindow = (day.BreakEndTime.Value, day.EndTime!.Value);

            if (firstWindow.Item1 < firstWindow.Item2) windows.Add(firstWindow);
            if (secondWindow.Item1 < secondWindow.Item2) windows.Add(secondWindow);
        }

        return windows;
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

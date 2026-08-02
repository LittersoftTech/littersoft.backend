using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Availability;

/// <summary>
/// Lays a provider service's day out as a timeline of blocks. Reads exactly what
/// <see cref="ProviderAvailabilitySlotService"/> reads — the offering's capacity,
/// the weekly working hours, this service's closures, and the same active
/// bookings — so the two surfaces always agree about what is occupied. The
/// difference is the shape: no duration is required, and every block is labelled
/// (free / booked / break / closed) rather than being filtered out.
/// </summary>
internal sealed class ProviderDailyAgendaService(
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IDailyAgendaReader agendaReader,
    IProviderClosureReader closureReader) : IProviderDailyAgendaService
{
    // A night stay owns the whole calendar day, so its single block spans it.
    private static readonly TimeOnly DayStart = TimeOnly.MinValue;
    private static readonly TimeOnly DayEnd = new(23, 59, 59);

    public async Task<ProviderDailyAgendaResult> GetDailyAgendaAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        Guid? callerPetParentId,
        CancellationToken cancellationToken)
    {
        // 1. Resolve the service: capacity, category, type. Same guards as the
        //    slot service — an unknown/inactive/foreign ServiceId is rejected
        //    rather than answered with an empty day.
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

        var bookings = await agendaReader.GetAgendaForDateAsync(serviceId, date, cancellationToken);
        var closures = await closureReader.GetActiveClosuresForDateAsync(serviceId, date, cancellationToken);
        var isClosedForDay = closures.Any(c => c.IsFullDay);

        // 2. NightStay is date-granular: a stay occupies its bucket for the whole
        //    night, and the create path never consults the weekly time grid. The
        //    day is therefore one block, and there are no opening hours to report.
        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            return BuildNightStayAgenda(providerId, serviceId, date, offering, bookings, isClosedForDay, callerPetParentId);
        }

        // 3. The weekly schedule frames the day. A closed weekday or an all-day
        //    closure leaves nothing to lay out.
        var weekly = await availabilityService.GetAsync(providerId, cancellationToken);
        var daySchedule = weekly.Days.FirstOrDefault(d => d.DayOfWeek == (int)date.DayOfWeek);

        if (daySchedule is null
            || !daySchedule.IsOpen
            || daySchedule.StartTime is null
            || daySchedule.EndTime is null
            || isClosedForDay)
        {
            return new ProviderDailyAgendaResult(
                providerId, serviceId, date,
                offering.ServiceCategory, offering.SubCategory, offering.ServiceType,
                offering.Capacity,
                IsOpen: false,
                IsClosedForDay: isClosedForDay,
                OpeningTime: daySchedule?.StartTime,
                ClosingTime: daySchedule?.EndTime,
                Entries: Array.Empty<AgendaEntry>());
        }

        var openingTime = daySchedule.StartTime.Value;
        var closingTime = daySchedule.EndTime.Value;

        // 4. Partition the working day. Closures win over the break where they
        //    overlap (being away is the stronger signal), so the blocks the
        //    parent sees never cover the same minute twice.
        var closedWindows = MergeWindows(
            closures
                .Where(c => c is { StartTime: not null, EndTime: not null })
                .Select(c => (c.StartTime!.Value, c.EndTime!.Value))
                .Select(w => Clip(w, openingTime, closingTime))
                .Where(w => w is not null)
                .Select(w => w!.Value));

        var breakWindows = daySchedule is { BreakStartTime: not null, BreakEndTime: not null }
            ? Subtract(
                Clip((daySchedule.BreakStartTime.Value, daySchedule.BreakEndTime.Value), openingTime, closingTime) is { } b
                    ? [b]
                    : [],
                closedWindows)
            : [];

        var openWindows = Subtract(Subtract([(openingTime, closingTime)], closedWindows), breakWindows);

        // 5. Lay out the bookable time, then fold the blocked windows back in so
        //    the timeline reads continuously from opening to closing. Time inside
        //    the booking lead-time window is still SHOWN — the parent is browsing
        //    the provider's day, and a job at 09:00 is part of that day whether or
        //    not it can still be booked — but it is not bookable, so the cutoff
        //    becomes a block boundary just like a break or a closure.
        var bookableFrom = BookableFrom(date, DateTimeOffset.UtcNow);

        var entries = new List<AgendaEntry>();
        foreach (var window in openWindows)
        {
            entries.AddRange(BuildBookedAndFreeEntries(
                window, bookings, offering.Capacity, callerPetParentId, bookableFrom));
        }

        entries.AddRange(closedWindows.Select(w => Blocked(w, AgendaEntryType.Closed, AgendaStatuses.Closed)));
        entries.AddRange(breakWindows.Select(w => Blocked(w, AgendaEntryType.Break, AgendaStatuses.Break)));
        entries.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        return new ProviderDailyAgendaResult(
            providerId, serviceId, date,
            offering.ServiceCategory, offering.SubCategory, offering.ServiceType,
            offering.Capacity,
            IsOpen: true,
            IsClosedForDay: false,
            openingTime,
            closingTime,
            entries);
    }

    private static ProviderDailyAgendaResult BuildNightStayAgenda(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        OfferingResolution.Resolved offering,
        IReadOnlyList<AgendaBookingRow> stays,
        bool isClosedForDay,
        Guid? callerPetParentId)
    {
        // A stay begins at drop-off on its check-in day, so the whole-day block is
        // bookable only if that instant clears the lead time. Expressed as a
        // day-wide "bookable from" so the shared Label stays uniform: DayStart
        // when the stay is far enough ahead, null when it isn't.
        var staysFarEnoughAhead = !BookingLeadTime.IsTooSoon(
            date, offering.DropOffTime ?? TimeOnly.MinValue, DateTimeOffset.UtcNow);
        TimeOnly? bookableFrom = staysFarEnoughAhead ? DayStart : null;

        var entries = isClosedForDay
            ? [Blocked((DayStart, DayEnd), AgendaEntryType.Closed, AgendaStatuses.Closed)]
            : new List<AgendaEntry>
            {
                Label((DayStart, DayEnd), stays, offering.Capacity, callerPetParentId, bookableFrom)
            };

        return new ProviderDailyAgendaResult(
            providerId, serviceId, date,
            offering.ServiceCategory, offering.SubCategory, offering.ServiceType,
            offering.Capacity,
            IsOpen: !isClosedForDay,
            isClosedForDay,
            // A stay is booked against nights, not clock time — there are no
            // opening hours gating it (mirrors the night-stay create path).
            OpeningTime: null,
            ClosingTime: null,
            entries);
    }

    /// <summary>
    /// Cuts one open working window at every booking boundary inside it, labels
    /// each resulting segment, then merges neighbours that read identically — so
    /// an untouched morning comes back as a single free block rather than a run
    /// of fragments.
    /// </summary>
    private static List<AgendaEntry> BuildBookedAndFreeEntries(
        (TimeOnly Start, TimeOnly End) window,
        IReadOnlyList<AgendaBookingRow> bookings,
        int capacity,
        Guid? callerPetParentId,
        TimeOnly? bookableFrom)
    {
        var boundaries = new SortedSet<TimeOnly> { window.Start, window.End };

        // Cut at the lead-time cutoff so no block straddles it — otherwise a free
        // morning, merged into one maximal block, would have to be called bookable
        // or not for its whole length when only its tail actually is.
        if (bookableFrom is { } from && from > window.Start && from < window.End)
        {
            boundaries.Add(from);
        }

        foreach (var booking in bookings)
        {
            if (booking.StartTime > window.Start && booking.StartTime < window.End)
            {
                boundaries.Add(booking.StartTime);
            }
            if (booking.EndTime > window.Start && booking.EndTime < window.End)
            {
                boundaries.Add(booking.EndTime);
            }
        }

        var cuts = boundaries.ToArray();
        var segments = new List<AgendaEntry>(cuts.Length);
        for (var i = 0; i < cuts.Length - 1; i++)
        {
            segments.Add(Label((cuts[i], cuts[i + 1]), bookings, capacity, callerPetParentId, bookableFrom));
        }

        return Merge(segments);
    }

    /// <summary>
    /// The earliest time ON <paramref name="date"/> that clears the booking
    /// lead time: <see cref="TimeOnly.MinValue"/> when the whole day is far
    /// enough ahead, a time-of-day when the cutoff lands inside it, and
    /// <c>null</c> when the entire day is already too soon (today, mostly).
    /// </summary>
    private static TimeOnly? BookableFrom(DateOnly date, DateTimeOffset nowUtc)
    {
        var earliestStart = BookingLeadTime.EarliestBookableStart(nowUtc);
        var dayStart = BookingLeadTime.ServiceStartUtc(date, TimeOnly.MinValue);

        if (earliestStart <= dayStart)
        {
            return TimeOnly.MinValue;
        }

        var intoDay = earliestStart - dayStart;
        return intoDay < TimeSpan.FromDays(1) ? TimeOnly.FromTimeSpan(intoDay) : null;
    }

    /// <summary>
    /// Labels one segment from the bookings overlapping it. Only the caller's own
    /// booking surfaces its real status and job id; anybody else's is flattened to
    /// a bare BOOKED block so one parent can't read another's appointments. A
    /// Custom walk-in has no PetParentId and therefore always masks.
    /// </summary>
    private static AgendaEntry Label(
        (TimeOnly Start, TimeOnly End) segment,
        IReadOnlyList<AgendaBookingRow> bookings,
        int capacity,
        Guid? callerPetParentId,
        TimeOnly? bookableFrom)
    {
        // Half-open overlap, matching the slot service and the create sproc.
        var overlapping = bookings
            .Where(b => b.StartTime < segment.End && b.EndTime > segment.Start)
            .ToArray();

        var remaining = Math.Max(0, capacity - overlapping.Length);

        // Capacity alone no longer settles bookability: a slot also has to clear
        // the booking lead time. Segments are cut at the cutoff, so testing the
        // segment's start is enough — it never straddles.
        var clearsLeadTime = bookableFrom is { } from && segment.Start >= from;
        var isBookable = remaining > 0 && clearsLeadTime;

        if (overlapping.Length == 0)
        {
            return new AgendaEntry(
                segment.Start, segment.End, AgendaEntryType.Free, AgendaStatuses.Free,
                JobId: null, BookingId: null, remaining, isBookable);
        }

        // `is { } caller` matters: a caller with no profile resolves to null, and a
        // null-to-null comparison would otherwise hand them every walk-in booking.
        var own = callerPetParentId is { } caller
            ? overlapping.FirstOrDefault(b => b.PetParentId == caller)
            : null;

        return new AgendaEntry(
            segment.Start,
            segment.End,
            AgendaEntryType.Booked,
            own?.Status ?? AgendaStatuses.Booked,
            own is null ? null : $"PF-{own.JobNumber:D6}",
            own?.BookingId,
            remaining,
            isBookable);
    }

    private static AgendaEntry Blocked((TimeOnly Start, TimeOnly End) window, AgendaEntryType type, string status)
        => new(window.Start, window.End, type, status, JobId: null, BookingId: null,
            RemainingCapacity: 0, IsBookable: false);

    /// <summary>Joins neighbouring segments that carry the same label, so the
    /// timeline is made of maximal blocks.</summary>
    private static List<AgendaEntry> Merge(List<AgendaEntry> segments)
    {
        var merged = new List<AgendaEntry>(segments.Count);
        foreach (var segment in segments)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                if (previous.EndTime == segment.StartTime
                    && previous.EntryType == segment.EntryType
                    && previous.Status == segment.Status
                    && previous.JobId == segment.JobId
                    && previous.BookingId == segment.BookingId
                    && previous.RemainingCapacity == segment.RemainingCapacity)
                {
                    merged[^1] = previous with { EndTime = segment.EndTime };
                    continue;
                }
            }

            merged.Add(segment);
        }

        return merged;
    }

    private static (TimeOnly Start, TimeOnly End)? Clip(
        (TimeOnly Start, TimeOnly End) window, TimeOnly lower, TimeOnly upper)
    {
        var start = window.Start < lower ? lower : window.Start;
        var end = window.End > upper ? upper : window.End;
        return start < end ? (start, end) : null;
    }

    /// <summary>Sorts and unions overlapping/touching windows.</summary>
    private static List<(TimeOnly Start, TimeOnly End)> MergeWindows(
        IEnumerable<(TimeOnly Start, TimeOnly End)> windows)
    {
        var ordered = windows.OrderBy(w => w.Start).ToList();
        var merged = new List<(TimeOnly Start, TimeOnly End)>(ordered.Count);

        foreach (var window in ordered)
        {
            if (merged.Count > 0 && window.Start <= merged[^1].End)
            {
                if (window.End > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, window.End);
                }
                continue;
            }

            merged.Add(window);
        }

        return merged;
    }

    /// <summary>Removes every window in <paramref name="cutouts"/> from
    /// <paramref name="windows"/>, splitting or trimming as needed.</summary>
    private static List<(TimeOnly Start, TimeOnly End)> Subtract(
        List<(TimeOnly Start, TimeOnly End)> windows,
        List<(TimeOnly Start, TimeOnly End)> cutouts)
    {
        if (cutouts.Count == 0)
        {
            return windows;
        }

        var current = windows;
        foreach (var (cs, ce) in cutouts)
        {
            var next = new List<(TimeOnly Start, TimeOnly End)>(current.Count);
            foreach (var (ws, we) in current)
            {
                if (ce <= ws || cs >= we)
                {
                    next.Add((ws, we));
                    continue;
                }

                if (cs > ws) next.Add((ws, cs));
                if (ce < we) next.Add((ce, we));
            }
            current = next;
        }

        return current;
    }
}

namespace Pawfront.Application.Events;

/// <summary>
/// Sort keys for the catalog listing (<c>GET /events</c>).
/// </summary>
/// <remarks>
/// <para>
/// Omitting the sort key entirely keeps the listing in the order
/// <c>Event.ListEvents</c> returns (newest start date first), which is what every
/// existing caller already gets — so the parameter is additive rather than a
/// change of default.
/// </para>
/// <para>
/// "Distance" is deliberately absent: it needs a reference coordinate the server
/// does not have, and half the catalog (online events) has no venue to measure
/// to at all.
/// </para>
/// </remarks>
public enum EventSortBy
{
    /// <summary>
    /// The event's start date + time. Ascending is the design's "Soonest First".
    /// </summary>
    Date = 0,

    /// <summary>
    /// Ticket price, with free events counted as zero — a free event IS the
    /// cheapest, so sorting it last would be wrong.
    /// </summary>
    Price = 1,

    /// <summary>
    /// Remaining capacity: the venue's maximum less the tickets already
    /// confirmed. Descending is "Most Available", ascending is "Almost Full".
    /// </summary>
    SeatsLeft = 2
}

/// <summary>
/// Parses and applies the catalog listing's sort. Application-side rather than in
/// <c>Event.ListEvents</c> because two of the three keys cannot be sorted in SQL:
/// the venue capacity behind <see cref="EventSortBy.SeatsLeft"/> lives in the
/// Cosmos extension document, which is hydrated after the query returns. Doing
/// the third one in SQL as well would leave two sort implementations that could
/// disagree about ties.
/// </summary>
public static class EventListSorting
{
    /// <summary>
    /// Blank means "no sort" — keep the store's own order. Throws
    /// <see cref="ArgumentException"/> on an unrecognised value so the endpoint
    /// can answer 400 instead of silently returning an order the caller did not
    /// ask for.
    /// </summary>
    public static EventSortBy? ParseSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<EventSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Date', 'Price' or 'SeatsLeft'.",
                nameof(value));
    }

    /// <summary>
    /// Seats still available on an event, or null when there is no limit.
    /// </summary>
    /// <remarks>
    /// An ONLINE event has no Cosmos document and therefore no capacity: it is
    /// genuinely unlimited, not "zero seats left". A physical event whose
    /// extension document could not be read is treated the same way — the read is
    /// best-effort by design, and inventing a seat count from a failed lookup
    /// would put real events at the wrong end of the list.
    ///
    /// Clamped at zero because a sold-out event whose capacity was later lowered
    /// can legitimately have more tickets than seats, and "-3 seats left" is not
    /// something to render.
    /// </remarks>
    public static int? SeatsLeft(EventResult result) =>
        result.Physical is null
            ? null
            : Math.Max(0, result.Physical.MaximumCapacity - result.TotalBookings);

    /// <summary>
    /// Applies the requested order to an already-hydrated page. A null
    /// <paramref name="sortBy"/> returns the input untouched.
    /// </summary>
    public static IReadOnlyCollection<EventResult> Apply(
        IReadOnlyCollection<EventResult> events,
        EventSortBy? sortBy,
        Earnings.EarningsSortDirection direction)
    {
        if (sortBy is null || events.Count <= 1)
        {
            return events;
        }

        var ascending = direction == Earnings.EarningsSortDirection.Ascending;

        IOrderedEnumerable<EventResult> ordered = sortBy switch
        {
            EventSortBy.Date => ascending
                ? events.OrderBy(e => e.StartDate).ThenBy(e => e.StartTime)
                : events.OrderByDescending(e => e.StartDate).ThenByDescending(e => e.StartTime),

            // Free events sort as zero rather than as "no price": free IS the
            // cheapest end of the scale, and a parent sorting low-to-high expects
            // to see them first.
            EventSortBy.Price => ascending
                ? events.OrderBy(e => e.Price ?? 0m)
                : events.OrderByDescending(e => e.Price ?? 0m),

            // Unlimited (online, or a physical event we could not read a capacity
            // for) is the most available there is, so it leads "Most Available"
            // and trails "Almost Full".
            _ => ascending
                ? events.OrderBy(e => SeatsLeft(e) ?? int.MaxValue)
                : events.OrderByDescending(e => SeatsLeft(e) ?? int.MaxValue)
        };

        // Deterministic tie-break — most events in a catalog share a price or a
        // date, and an unstable order makes the list jump between refreshes.
        return ordered.ThenBy(e => e.EventId).ToArray();
    }
}

namespace Pawfront.Application.Availability;

/// <summary>
/// A provider service's whole day laid out as a contiguous, non-overlapping
/// timeline of blocks — what is taken, what is free, and how much room is left
/// in each. Backs the parent-facing "pick a slot" screen: unlike
/// <see cref="AvailableSlotsResult"/>, it needs no duration up front, so the
/// parent can look at the day before deciding what to book.
/// <para>
/// <see cref="Entries"/> covers only the provider's working hours for the date
/// (or, for a NightStay service, the whole day as one block — a stay occupies
/// its bucket for the entire night). Time outside those hours is simply absent;
/// <see cref="OpeningTime"/> / <see cref="ClosingTime"/> frame it.
/// </para>
/// </summary>
public sealed record ProviderDailyAgendaResult(
    Guid ProviderId,
    Guid ServiceId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    // The offering's capacity — how many pets the provider can serve at once on
    // this service. Every entry's RemainingCapacity is measured against it.
    int Capacity,
    // False when the provider's weekly schedule has this weekday closed, or an
    // all-day closure (sick leave / vacation) covers the date. Entries is empty.
    bool IsOpen,
    // True specifically when an all-day closure covers the date (IsOpen is then
    // false too) — lets the client say "away" rather than "not a working day".
    bool IsClosedForDay,
    TimeOnly? OpeningTime,
    TimeOnly? ClosingTime,
    IReadOnlyList<AgendaEntry> Entries);

/// <summary>
/// One block of the day. Blocks are contiguous and never overlap: the day is cut
/// at every booking boundary and adjacent blocks that read the same are merged
/// back together, so a run of free time is one entry rather than many.
/// <para>
/// <see cref="Status"/> carries the job's real lifecycle status
/// (<c>CONFIRMED</c>, <c>IN_PROGRESS</c>, …) only for a booking belonging to the
/// caller; another parent's booking reads <c>BOOKED</c> and yields no
/// <see cref="JobId"/> / <see cref="BookingId"/>. Non-job blocks use
/// <c>FREE</c>, <c>BREAK</c> or <c>CLOSED</c>.
/// </para>
/// <para>
/// <see cref="RemainingCapacity"/> is the offering's capacity minus the bookings
/// overlapping this block — the same overlap count the race-safe create sproc
/// uses, so what shows here is what create will admit. It is why an occupied
/// block can still be bookable: a 3-pet daycare with one job at 14:00 has two
/// places left.
/// </para>
/// </summary>
public sealed record AgendaEntry(
    TimeOnly StartTime,
    TimeOnly EndTime,
    AgendaEntryType EntryType,
    string Status,
    // "PF-000123" — the caller's own booking only.
    string? JobId,
    // The caller's own booking only. Feeds GET /pet-parents/{id}/bookings/{id}.
    Guid? BookingId,
    int RemainingCapacity,
    bool IsBookable);

/// <summary>
/// The <see cref="AgendaEntry.Status"/> values that are not a booking's own
/// lifecycle status. Uppercase, matching the booking-status convention.
/// </summary>
public static class AgendaStatuses
{
    public const string Free = "FREE";

    /// <summary>Occupied by a booking that is not the caller's — deliberately
    /// says nothing about whose it is or how far along it is.</summary>
    public const string Booked = "BOOKED";

    public const string Break = "BREAK";
    public const string Closed = "CLOSED";
}

/// <summary>The coarse bucket a block falls into, so clients can branch without
/// string-matching <see cref="AgendaEntry.Status"/>.</summary>
public enum AgendaEntryType
{
    /// <summary>Nothing booked here.</summary>
    Free,

    /// <summary>At least one active booking overlaps. Still bookable while
    /// <see cref="AgendaEntry.RemainingCapacity"/> is positive.</summary>
    Booked,

    /// <summary>The provider's daily break.</summary>
    Break,

    /// <summary>A partial-day closure (sick leave / vacation) on this service.</summary>
    Closed
}

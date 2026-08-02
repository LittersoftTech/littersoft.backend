namespace Pawfront.Contracts.Availability;

/// <summary>
/// A provider service's whole day as a contiguous timeline of blocks — what's
/// taken, what's free, and how much room is left in each. Unlike the free-slots
/// response this needs no duration up front, so the parent can look at the day
/// before deciding what to book.
/// </summary>
public sealed record ProviderDailyAgendaResponse(
    Guid ProviderId,
    Guid ServiceId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    // How many pets the provider can serve at once on this service. Every
    // entry's remainingCapacity is measured against it.
    int Capacity,
    // False when the weekday is closed in the provider's weekly schedule or an
    // all-day closure covers the date — entries is then empty.
    bool IsOpen,
    // True specifically when an all-day closure (sick leave / vacation) covers
    // the date, so the client can say "away" rather than "not a working day".
    bool IsClosedForDay,
    // The day's working window. Null for a NightStay service, which is booked
    // against nights rather than clock time.
    TimeOnly? OpeningTime,
    TimeOnly? ClosingTime,
    IReadOnlyList<AgendaEntryResponse> Entries);

/// <summary>
/// One block of the day. Blocks are contiguous and never overlap, and adjacent
/// blocks that read the same are merged — so an untouched morning is one entry,
/// not a run of fragments.
/// </summary>
/// <param name="EntryType">
/// <c>Free</c> | <c>Booked</c> | <c>Break</c> | <c>Closed</c> — branch on this
/// rather than string-matching <paramref name="Status"/>.
/// </param>
/// <param name="Status">
/// The job's real lifecycle status (<c>CONFIRMED</c>, <c>IN_PROGRESS</c>, …) for
/// a booking of the CALLER'S OWN; another parent's booking reads <c>BOOKED</c>
/// and carries no ids. Non-job blocks read <c>FREE</c> / <c>BREAK</c> /
/// <c>CLOSED</c>.
/// </param>
/// <param name="JobId">Friendly job id ("PF-000123") — caller's own booking only.</param>
/// <param name="BookingId">Caller's own booking only; feeds the booking-detail read.</param>
/// <param name="RemainingCapacity">
/// Capacity minus the bookings overlapping this block — the same overlap count
/// the race-safe create sproc uses, so what's shown is what create will admit.
/// This is why a <c>Booked</c> block can still be bookable: a 3-pet daycare with
/// one job at 14:00 has two places left.
/// </param>
/// <param name="IsBookable">Shorthand for "remainingCapacity &gt; 0 and not blocked".</param>
public sealed record AgendaEntryResponse(
    TimeOnly StartTime,
    TimeOnly EndTime,
    string EntryType,
    string Status,
    string? JobId,
    Guid? BookingId,
    int RemainingCapacity,
    bool IsBookable);

namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow SQL reader for provider booking statistics surfaced on the
/// parent-facing search cards. Kept separate from <c>IBookingService</c>
/// (transactional flows) — this is a read-only aggregate.
/// </summary>
public interface IProviderBookingStatsReader
{
    /// <summary>
    /// Per-provider count of bookings that are either explicitly COMPLETED or have
    /// already finished, and were not cancelled or no-shows, across ALL of each
    /// provider's services (any category, freelance or business). Counts both
    /// single-day bookings (finished = BookingDate/EndTime in the past) and
    /// multi-night boarding stays (finished = CheckOutDate in the past — the
    /// checkout day is the pickup day, not a stayed night). Providers with zero
    /// completed bookings are simply absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetCompletedBookingCountsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}

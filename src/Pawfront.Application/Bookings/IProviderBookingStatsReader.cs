namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow SQL reader for provider booking statistics surfaced on the
/// parent-facing search cards. Kept separate from <c>IBookingService</c>
/// (transactional flows) — this is a read-only aggregate.
/// </summary>
public interface IProviderBookingStatsReader
{
    /// <summary>
    /// Per-provider count of bookings that have already finished
    /// (BookingDate/EndTime in the past) and were not cancelled or no-shows,
    /// across ALL of each provider's services. Providers with zero completed
    /// bookings are simply absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetCompletedBookingCountsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}

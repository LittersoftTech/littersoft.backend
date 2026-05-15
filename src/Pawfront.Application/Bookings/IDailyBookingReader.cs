namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow read interface consumed by the slot service. Returns the date's confirmed booking
/// windows so the slot algorithm can count overlaps against capacity.
/// </summary>
public interface IDailyBookingReader
{
    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken);
}

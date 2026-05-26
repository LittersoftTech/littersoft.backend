namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow read interface consumed by the slot service. Returns the date's confirmed booking
/// windows so the slot algorithm can count overlaps against capacity. Scoped by ServiceId
/// — DayCare and NightStay slot grids on the same provider count overlaps independently.
/// </summary>
public interface IDailyBookingReader
{
    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken);
}

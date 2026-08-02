namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow read interface consumed by the slot service for NightStay
/// availability. Night-stay capacity is per NIGHT, not per time window — this
/// returns how many active stays (not cancelled/declined) cover each night in
/// the requested range (CheckInDate &lt;= night &lt; CheckOutDate). Mirror of
/// <see cref="IDailyBookingReader"/> for the multi-night boarding model.
/// </summary>
public interface INightStayOccupancyReader
{
    /// <summary>
    /// Active-stay count per night over <paramref name="fromNight"/> ..
    /// <paramref name="toNight"/> (both inclusive — each entry is a stayed
    /// night). Every night in the range is present in the result, zero
    /// occupancy included.
    /// </summary>
    Task<IReadOnlyDictionary<DateOnly, int>> GetNightlyOccupancyAsync(
        Guid serviceId,
        DateOnly fromNight,
        DateOnly toNight,
        CancellationToken cancellationToken);
}

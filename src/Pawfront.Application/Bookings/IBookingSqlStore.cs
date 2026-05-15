namespace Pawfront.Application.Bookings;

/// <summary>
/// Low-level SQL operations on <c>Booking.Bookings</c>. Used by <see cref="BookingService"/>
/// and indirectly by the slot service via <see cref="IDailyBookingReader"/>.
/// </summary>
public interface IBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The stored proc holds UPDLOCK + HOLDLOCK on the overlap-count query
    /// and rejects the insert when concurrent bookings have already filled the requested slot.
    /// Throws <see cref="BookingCapacityExceededException"/> when full,
    /// <see cref="BookingProviderNotFoundException"/> if the provider is gone, and
    /// <see cref="BookingPetParentNotFoundException"/> if the parent is gone.
    /// </summary>
    Task<BookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        string serviceCategory,
        string subCategory,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        int capacity,
        CancellationToken cancellationToken);

    Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid providerId,
        DateOnly bookingDate,
        CancellationToken cancellationToken);
}

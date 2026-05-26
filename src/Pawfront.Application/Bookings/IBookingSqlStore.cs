namespace Pawfront.Application.Bookings;

/// <summary>
/// Low-level SQL operations on <c>Booking.Bookings</c>. Used by <see cref="BookingService"/>
/// and indirectly by the slot service via <see cref="IDailyBookingReader"/>.
/// </summary>
public interface IBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The stored proc validates the ServiceId belongs to the provider
    /// and is active, then holds UPDLOCK + HOLDLOCK on the overlap-count query for that
    /// service and rejects the insert when concurrent bookings have already filled the
    /// requested slot.
    /// Throws <see cref="BookingCapacityExceededException"/> when full,
    /// <see cref="BookingProviderNotFoundException"/> if the provider is gone,
    /// <see cref="BookingPetParentNotFoundException"/> if the parent is gone, or
    /// <see cref="BookingServiceInvalidException"/> if the ServiceId is unknown,
    /// inactive, or not owned by the provider.
    /// </summary>
    Task<BookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid serviceId,
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
        DateOnly? date,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken);
}

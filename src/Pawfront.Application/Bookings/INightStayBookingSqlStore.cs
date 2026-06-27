namespace Pawfront.Application.Bookings;

/// <summary>
/// Low-level SQL operations on <c>Booking.NightStayBookings</c>. Mirror of
/// <see cref="IBookingSqlStore"/> for the multi-night boarding model.
/// </summary>
public interface INightStayBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The stored proc validates the ServiceId belongs to the
    /// provider, is active, and is a NightStay service, then enforces per-night
    /// capacity under UPDLOCK + HOLDLOCK across <c>[checkInDate, checkOutDate)</c>.
    /// Maps the sproc THROWs 51230–51235 to the matching typed exceptions.
    /// </summary>
    Task<NightStayBookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        TimeOnly dropOffTime,
        TimeOnly pickUpTime,
        int capacity,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Race-safe status change + audit insert. Maps the sproc THROWs (51240 not
    /// found, 51241 forbidden, 51242 not allowed for actor, 51243 terminal,
    /// 51244 unchanged, 51245 invalid actor/status) to the matching exceptions.
    /// </summary>
    Task<NightStayBookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

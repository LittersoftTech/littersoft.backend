namespace Pawfront.Application.Bookings;

public interface IBookingService
{
    Task<BookingResult> CreateAsync(CreateBookingCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Provider-added private/custom booking for an unregistered walk-in
    /// customer. Counts against the same per-service capacity bucket as app
    /// bookings; the per-offering duration-rule check is skipped because the
    /// provider is in direct control of the time window.
    /// </summary>
    Task<BookingResult> CreateCustomAsync(
        CreateCustomBookingCommand command,
        CancellationToken cancellationToken);

    Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List the provider's bookings. <paramref name="date"/> narrows to a single
    /// calendar day when provided (the provider's "today" view); omit it to return
    /// the full history.
    /// </summary>
    Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a booking to a new lifecycle status and writes an audit row, both in
    /// one transaction. The actor + their id are enforced against the booking
    /// (forbidden otherwise), the status must be one the actor may set, and the
    /// booking must not already be terminal. Returns the updated booking.
    /// Throws <see cref="UnsupportedBookingStatusException"/> (unknown status),
    /// <see cref="BookingNotFoundException"/>, <see cref="BookingStatusForbiddenException"/>,
    /// <see cref="BookingStatusNotAllowedException"/>,
    /// <see cref="BookingStatusTerminalException"/>, or
    /// <see cref="BookingStatusUnchangedException"/>.
    /// </summary>
    Task<BookingResult> UpdateStatusAsync(
        UpdateBookingStatusCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full status-change audit trail for a booking, oldest-first
    /// (the seeded creation entry is first). Empty when the booking has no
    /// history (or doesn't exist) — list semantics, no exception.
    /// </summary>
    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

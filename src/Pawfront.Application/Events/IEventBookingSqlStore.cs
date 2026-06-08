namespace Pawfront.Application.Events;

public interface IEventBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The sproc holds UPDLOCK + HOLDLOCK on the SUM of
    /// confirmed ticket counts for the event, so concurrent calls serialise
    /// and the (N+1)-th seat is rejected once <paramref name="input.MaximumCapacity"/>
    /// is reached.
    /// Throws <see cref="EventBookingEventNotFoundException"/> if the event row is gone or
    /// <see cref="EventBookingCapacityExceededException"/> if the event is full.
    /// </summary>
    Task<EventBookingResult> CreateAsync(
        CreateEventBookingSqlInput input,
        CancellationToken cancellationToken);

    Task<EventBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// External gateway callback. Sets PaymentStatus + PaymentReference.
    /// Throws <see cref="EventBookingNotFoundException"/> if the booking is gone or
    /// <see cref="EventBookingPaymentAlreadyConfirmedException"/> if the booking was
    /// already finalised with a different result.
    /// </summary>
    Task<EventBookingResult> ConfirmPaymentAsync(
        Guid bookingId,
        string paymentStatus,
        string? paymentReference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the joined booking/ticket rows for one event, restricted to
    /// bookings owned by <paramref name="providerId"/>. Throws
    /// <see cref="EventNotFoundForProviderException"/> on ownership miss.
    /// </summary>
    Task<IReadOnlyList<EventAttendee>> ListAttendeesAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the event counter columns + confirmed-paid booking aggregates
    /// in one round-trip. Throws <see cref="EventNotFoundForProviderException"/>
    /// on ownership miss.
    /// </summary>
    Task<EventMetrics> GetMetricsAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists all event bookings made under <paramref name="bookerEmail"/>,
    /// joined with their event so the mobile summary card can render
    /// without a follow-up fetch. Ordered most-recent first. Cancelled
    /// bookings are included so the parent sees their full history.
    /// </summary>
    Task<IReadOnlyList<EventBookingSummary>> ListByBookerEmailAsync(
        string bookerEmail,
        CancellationToken cancellationToken);
}

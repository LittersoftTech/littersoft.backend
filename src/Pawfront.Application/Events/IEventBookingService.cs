namespace Pawfront.Application.Events;

public interface IEventBookingService
{
    /// <summary>
    /// Creates an event ticket booking. Capacity is enforced by the SQL sproc
    /// under UPDLOCK + HOLDLOCK, so concurrent buyers are serialised and the
    /// (N+1)-th seat is rejected once the event's MaximumCapacity is reached.
    /// </summary>
    Task<EventBookingResult> CreateAsync(
        CreateEventBookingCommand command,
        CancellationToken cancellationToken);

    /// <summary>Returns the booking with one entry per ticket, or null if unknown.</summary>
    Task<EventBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// External payment-gateway callback. Flips PaymentStatus to Paid or Failed
    /// and records the gateway reference. Idempotent for redelivery of the same
    /// (status, reference) pair.
    /// </summary>
    Task<EventBookingResult> ConfirmPaymentAsync(
        Guid bookingId,
        string paymentStatus,
        string? paymentReference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Organiser-only attendee list for one event. Verifies the event belongs
    /// to <paramref name="providerId"/>. One row per ticket; Cancelled
    /// bookings are excluded but Pending/Failed payments are returned with
    /// their <c>PaymentStatus</c> so the organiser can see who hasn't paid.
    /// Throws <see cref="EventNotFoundForProviderException"/> if the event is
    /// unknown OR owned by someone else.
    /// </summary>
    Task<IReadOnlyList<EventAttendee>> ListAttendeesAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Organiser-only metrics rollup. Combines the three event-level counters
    /// with confirmed-attendees + earnings aggregated from confirmed paid
    /// bookings. Throws <see cref="EventNotFoundForProviderException"/> if
    /// the event is unknown OR owned by someone else.
    /// </summary>
    Task<EventMetrics> GetMetricsAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists all event bookings made under <paramref name="bookerEmail"/>.
    /// Powers the pet-parent host's "my event bookings" screen — the
    /// endpoint pulls the email from the caller's Firebase JWT.
    /// </summary>
    Task<IReadOnlyList<EventBookingSummary>> ListByBookerEmailAsync(
        string bookerEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct set of event ids the caller currently holds
    /// (non-cancelled) tickets for, matched by <paramref name="bookerEmail"/>.
    /// Powers the <c>IsBookable</c> flag on event list / detail reads. Returns
    /// an empty set when the email is null/blank (no known bookings).
    /// </summary>
    Task<IReadOnlySet<Guid>> ListBookedEventIdsByBookerEmailAsync(
        string? bookerEmail,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-cancels the booking on behalf of the booker. Ownership is proven
    /// by matching <paramref name="bookerEmail"/> (from the caller's Firebase
    /// JWT) against the booking's free-text booker email — there is no FK to
    /// any user table, so either host's caller can cancel their own booking.
    /// Flips Status to Cancelled + stamps CancelledAtUtc, which releases the
    /// seat capacity automatically (the create-side capacity SUM only counts
    /// Confirmed rows). Throws
    /// <see cref="EventBookingNotFoundForBookerException"/> if the booking is
    /// unknown or not theirs, or
    /// <see cref="EventBookingAlreadyCancelledException"/> if it was already
    /// cancelled.
    /// </summary>
    Task<EventBookingResult> CancelByBookerAsync(
        Guid bookingId,
        string bookerEmail,
        CancellationToken cancellationToken);
}

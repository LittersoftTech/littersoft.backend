namespace Pawfront.Application.Events;

public sealed record CreateEventBookingCommand(
    Guid EventId,
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    IReadOnlyList<string> AttendeeNames,
    string PaymentMethod);

public sealed record EventBookingResult(
    Guid BookingId,
    Guid EventId,
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    int TicketCount,
    string PaymentMethod,
    string PaymentStatus,
    string? PaymentReference,
    decimal TotalAmount,
    string Status,
    IReadOnlyList<EventBookingTicket> Tickets,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

public sealed record EventBookingTicket(
    Guid TicketId,
    int TicketNumber,
    string AttendeeName);

/// <summary>
/// Wire shape sent from <see cref="EventBookingService"/> to <see cref="IEventBookingSqlStore"/>.
/// The store doesn't repeat the validation that the service does.
/// </summary>
public sealed record CreateEventBookingSqlInput(
    Guid EventId,
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    string PaymentMethod,
    int MaximumCapacity,
    decimal TotalAmount,
    IReadOnlyList<string> AttendeeNames);

public sealed class EventBookingEventNotFoundException(Guid eventId)
    : Exception($"Event '{eventId}' was not found.");

public sealed class EventBookingNotPhysicalException(Guid eventId)
    : Exception($"Event '{eventId}' is not a physical event and cannot accept ticket bookings.");

public sealed class EventBookingCapacityExceededException(Guid eventId, int maximumCapacity)
    : Exception($"Event '{eventId}' is sold out (capacity {maximumCapacity}).")
{
    public Guid EventId { get; } = eventId;
    public int MaximumCapacity { get; } = maximumCapacity;
}

public sealed class EventBookingNotFoundException(Guid bookingId)
    : Exception($"Event booking '{bookingId}' was not found.");

public sealed class EventBookingPaymentAlreadyConfirmedException(Guid bookingId)
    : Exception($"Event booking '{bookingId}' payment has already been confirmed.");

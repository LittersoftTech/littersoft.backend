namespace Pawfront.Contracts.Events;

public sealed record CreateEventBookingRequest(
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    IReadOnlyList<string> AttendeeNames,
    string PaymentMethod);

public sealed record ConfirmEventBookingPaymentRequest(
    string PaymentStatus,
    string? PaymentReference);

public sealed record EventBookingTicketResponse(
    Guid TicketId,
    int TicketNumber,
    string AttendeeName);

public sealed record EventBookingResponse(
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
    IReadOnlyList<EventBookingTicketResponse> Tickets,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

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

/// <summary>
/// Slim summary card returned by the parent host's "my event bookings"
/// endpoint. Joined with the event row so the mobile UI can render without
/// a follow-up fetch. The full attendee list is available via
/// <c>GET /event-bookings/{bookingId}</c> when the parent drills in.
/// </summary>
public sealed record EventBookingSummaryResponse(
    Guid BookingId,
    Guid EventId,
    string EventTitle,
    string EventCategory,
    DateOnly EventStartDate,
    TimeOnly EventStartTime,
    string? EventBannerImageUrl,
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    int TicketCount,
    string PaymentMethod,
    string PaymentStatus,
    string? PaymentReference,
    decimal TotalAmount,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

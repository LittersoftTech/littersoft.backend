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
    string AttendeeName, string PaymentStatus = "Pending", decimal Amount = 0);

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
    DateTimeOffset? CancelledAtUtc, int ConsecutiveNegativeMarks = 0, bool HasReported = false)
{
    public DateTimeOffset? CancelledAt => CancelledAtUtc;
    public bool EventCancelledByMe => Status is "CancelledByParent" or "Cancelled";
    public bool IsCancelledByHost => Status is "host_cancelled" or "EventCancelled";
}

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
    string EventType,
    DateOnly EventStartDate,
    TimeOnly EventStartTime,
    string? EventBannerImageUrl,
    // Venue address for physical events; null for online events (no venue).
    EventLocationResponse? EventLocation,
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
    DateTimeOffset? CancelledAtUtc,
    EventBookingStatsResponse Bookings,
    DateTimeOffset BookingsCloseAtUtc)
{
    public DateTimeOffset? CancelledAt => CancelledAtUtc;
    public bool EventCancelledByMe => Status is "CancelledByParent" or "Cancelled";
    public bool IsCancelledByHost => Status is "host_cancelled" or "EventCancelled";
}

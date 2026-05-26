namespace Pawfront.Contracts.Events;

public sealed record EventCountersResponse(
    int ViewCount,
    int ShareCount,
    int InquiryCount);

public sealed record EventMetricsResponse(
    int Views,
    int Shares,
    int Inquiries,
    int ConfirmedAttendees,
    decimal Earnings);

public sealed record EventAttendeeResponse(
    Guid TicketId,
    Guid BookingId,
    int TicketNumber,
    string AttendeeName,
    string BookerName,
    string BookerEmail,
    string? BookerMobile,
    string PaymentMethod,
    string PaymentStatus,
    decimal TotalAmount,
    DateTimeOffset CreatedAtUtc);

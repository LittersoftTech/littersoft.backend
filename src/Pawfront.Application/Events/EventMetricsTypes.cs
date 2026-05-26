namespace Pawfront.Application.Events;

/// <summary>
/// Wire shape of the three event-level counters bumped via the public
/// increment endpoints. Matches the columns on <c>Event.Events</c>.
/// </summary>
public sealed record EventCounterType
{
    public const string View = "View";
    public const string Share = "Share";
    public const string Inquiry = "Inquiry";
}

public sealed record EventCounters(
    int ViewCount,
    int ShareCount,
    int InquiryCount);

public sealed record EventMetrics(
    int Views,
    int Shares,
    int Inquiries,
    int ConfirmedAttendees,
    decimal Earnings);

public sealed record EventAttendee(
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

/// <summary>
/// Thrown when an organiser-scoped read (metrics / attendees) targets an
/// event that doesn't exist OR isn't owned by the requesting provider.
/// We don't distinguish the two by design to avoid leaking existence.
/// </summary>
public sealed class EventNotFoundForProviderException(Guid providerId, Guid eventId)
    : Exception($"Event '{eventId}' was not found for provider '{providerId}'.");

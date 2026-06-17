using Pawfront.Domain.Events;

namespace Pawfront.Application.Events;

internal sealed class EventBookingService(
    IEventService eventService,
    IEventBookingSqlStore sqlStore) : IEventBookingService
{
    private static readonly IReadOnlySet<string> AllowedPaymentMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "CreditCard",
        "Twint"
    };

    private static readonly IReadOnlySet<string> AllowedPaymentStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Paid",
        "Failed"
    };

    public async Task<EventBookingResult> CreateAsync(
        CreateEventBookingCommand command,
        CancellationToken cancellationToken)
    {
        var bookerName = Required(command.BookerName, nameof(command.BookerName));
        var bookerEmail = Required(command.BookerEmail, nameof(command.BookerEmail));
        var bookerMobile = Trim(command.BookerMobile);
        var paymentMethod = NormalizeOne(command.PaymentMethod, AllowedPaymentMethods, nameof(command.PaymentMethod));
        var attendeeNames = NormalizeAttendees(command.AttendeeNames);

        // Resolve the event (SQL row + Cosmos extension) — we need MaximumCapacity
        // (from the physical block) and ticketing (top-level) to compute the
        // total. IEventService already composes both.
        var @event = await eventService.GetAsync(command.EventId, cancellationToken)
            ?? throw new EventBookingEventNotFoundException(command.EventId);

        int? maximumCapacity;
        if (string.Equals(@event.EventType, nameof(EventType.Online), StringComparison.Ordinal))
        {
            // Online events have no venue capacity — a ticket is just the
            // signed-in attendee's seat, so exactly ONE ticket per booking.
            // Capacity is unlimited (the sproc skips the check when null).
            if (attendeeNames.Count != 1)
            {
                throw new EventBookingOnlineSingleTicketException(command.EventId);
            }
            maximumCapacity = null;
        }
        else
        {
            // Physical events allow ANY number of tickets, gated against the
            // venue capacity held in the Cosmos extension doc.
            if (@event.Physical is null)
            {
                throw new EventBookingNotPhysicalException(command.EventId);
            }
            maximumCapacity = @event.Physical.MaximumCapacity;
        }

        var totalAmount = @event.IsPaid
            ? (@event.Price ?? 0m) * attendeeNames.Count
            : 0m;

        return await sqlStore.CreateAsync(
            new CreateEventBookingSqlInput(
                command.EventId,
                bookerName,
                bookerEmail,
                bookerMobile,
                paymentMethod,
                maximumCapacity,
                totalAmount,
                attendeeNames),
            cancellationToken);
    }

    public Task<EventBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetAsync(bookingId, cancellationToken);

    public Task<EventBookingResult> ConfirmPaymentAsync(
        Guid bookingId,
        string paymentStatus,
        string? paymentReference,
        CancellationToken cancellationToken)
    {
        var normalised = NormalizeOne(paymentStatus, AllowedPaymentStatuses, nameof(paymentStatus));
        return sqlStore.ConfirmPaymentAsync(bookingId, normalised, Trim(paymentReference), cancellationToken);
    }

    public Task<IReadOnlyList<EventAttendee>> ListAttendeesAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken)
        => sqlStore.ListAttendeesAsync(providerId, eventId, cancellationToken);

    public Task<EventMetrics> GetMetricsAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken)
        => sqlStore.GetMetricsAsync(providerId, eventId, cancellationToken);

    public Task<IReadOnlyList<EventBookingSummary>> ListByBookerEmailAsync(
        string bookerEmail,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bookerEmail))
        {
            throw new ArgumentException("Booker email is required.", nameof(bookerEmail));
        }

        return sqlStore.ListByBookerEmailAsync(bookerEmail.Trim(), cancellationToken);
    }

    private static List<string> NormalizeAttendees(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            throw new ArgumentException(
                "AttendeeNames must contain at least one attendee name.",
                nameof(values));
        }

        var trimmed = new List<string>(values.Count);
        foreach (var raw in values)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "AttendeeNames cannot contain blank entries.",
                    nameof(values));
            }
            trimmed.Add(name);
        }
        return trimmed;
    }

    private static string NormalizeOne(string? value, IReadOnlySet<string> allowed, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }
        if (!allowed.Contains(trimmed))
        {
            throw new ArgumentException($"{fieldName} value '{trimmed}' is not supported.", fieldName);
        }
        return trimmed;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
        return value.Trim();
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

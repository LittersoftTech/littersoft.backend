using Pawfront.Application.Bookings;

namespace Pawfront.Application.Notifications;

/// <inheritdoc />
public sealed class BookingNotificationService(INotificationPublisher publisher)
    : IBookingNotificationService
{
    // Times are handed over as raw UTC instants, never as formatted strings: the
    // dispatcher converts them to the recipient's timezone and formats them at
    // render time, so a notification enqueued here reads identically to one
    // enqueued by a T-SQL sweep. See NotificationLocalTime.

    public Task NotifyBookingRequestedAsync(
        BookingResult booking,
        string? serviceType,
        string? petName,
        CancellationToken cancellationToken)
    {
        var serviceStartUtc = BookingLeadTime.ServiceStartUtc(booking.BookingDate, booking.StartTime);

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationDataKeys.BookingId] = booking.BookingId.ToString(),
            [NotificationDataKeys.BookingType] = "SingleDay",
            [NotificationDataKeys.ServiceStartUtc] = NotificationLocalTime.ToIso(serviceStartUtc),
            // Not shown in the body any more, but still handed to the app so it
            // can label the booking in its own UI without a second lookup.
            [NotificationDataKeys.ServiceName] = BookingServiceLabel.Resolve(serviceType, booking.ServiceItemCode)
        };

        AddCanonicalIds(data, booking.ProviderId, booking.PetParentId, booking.PetId, isNightStay: false);
        AddPetName(data, petName);
        AddAcceptByDeadline(
            data,
            BookingAcceptanceDeadline.Compute(booking.CreatedAtUtc, serviceStartUtc));

        return publisher.PublishAsync(
            new NotificationRequest(
                NotificationAudience.Provider,
                booking.ProviderId,
                NotificationTypes.BookingRequested,
                NotificationEntityTypes.Booking,
                booking.BookingId,
                data,
                DedupeKey: $"{NotificationTypes.BookingRequested}:{booking.BookingId}"),
            cancellationToken);
    }

    public Task NotifyNightStayBookingRequestedAsync(
        NightStayBookingResult booking,
        string? petName,
        CancellationToken cancellationToken)
    {
        // A stay's service begins at drop-off on the check-in day — the same
        // instant BR-53 and the lead-time rule measure against.
        var serviceStartUtc = BookingLeadTime.ServiceStartUtc(booking.CheckInDate, booking.DropOffTime);
        var checkOutUtc = BookingLeadTime.ServiceStartUtc(booking.CheckOutDate, booking.PickUpTime);

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationDataKeys.BookingId] = booking.NightStayBookingId.ToString(),
            [NotificationDataKeys.BookingType] = "NightStay",
            [NotificationDataKeys.ServiceStartUtc] = NotificationLocalTime.ToIso(serviceStartUtc),
            [NotificationDataKeys.CheckOutUtc] = NotificationLocalTime.ToIso(checkOutUtc),
            // Not shown in the body any more, but still handed to the app so it
            // can label the booking in its own UI without a second lookup.
            [NotificationDataKeys.ServiceName] =
                BookingServiceLabel.ResolveNightStay(booking.CheckInDate, booking.CheckOutDate)
        };

        AddCanonicalIds(data, booking.ProviderId, booking.PetParentId, booking.PetId, isNightStay: true);
        AddPetName(data, petName);
        AddAcceptByDeadline(
            data,
            BookingAcceptanceDeadline.Compute(booking.CreatedAtUtc, serviceStartUtc));

        return publisher.PublishAsync(
            new NotificationRequest(
                NotificationAudience.Provider,
                booking.ProviderId,
                NotificationTypes.NightStayBookingRequested,
                NotificationEntityTypes.NightStayBooking,
                booking.NightStayBookingId,
                data,
                DedupeKey: $"{NotificationTypes.NightStayBookingRequested}:{booking.NightStayBookingId}"),
            cancellationToken);
    }

    /// <summary>
    /// Adds the accept-by deadline as a UTC instant. The renderer turns it into the
    /// <c>acceptBy</c> string shown in the body, in the recipient's timezone; the
    /// instant itself also reaches the app, which needs it to run a countdown.
    /// </summary>
    private static void AddAcceptByDeadline(IDictionary<string, string> data, DateTimeOffset deadlineUtc) =>
        data[NotificationDataKeys.AcceptByUtc] = NotificationLocalTime.ToIso(deadlineUtc);

    /// <summary>
    /// Adds the canonical id block the mobile apps read on every notification.
    ///
    /// The SQL producers get these from
    /// <c>Notification.EnqueueBookingNotification</c>; this is the C# side of the
    /// same contract, and the two must agree — a notification that reached the app
    /// without a <c>providerId</c> just because it happened to be enqueued from C#
    /// would be an invisible difference to debug.
    ///
    /// <c>category</c> is NOT set here: <see cref="NotificationPayloadBuilder"/>
    /// writes it from the template, so it can never disagree with the type.
    /// </summary>
    private static void AddCanonicalIds(
        IDictionary<string, string> data,
        Guid providerId,
        Guid? petParentId,
        Guid? petId,
        bool isNightStay)
    {
        data[NotificationDataKeys.ProviderId] = providerId.ToString();
        data[NotificationDataKeys.IsNightStay] = isNightStay ? "true" : "false";

        // Absent rather than empty: the payload builder fills every canonical
        // field it doesn't find, so writing "" here would just duplicate that.
        if (petParentId is { } parentId)
        {
            data[NotificationDataKeys.ParentId] = parentId.ToString();
        }

        if (petId is { } pet)
        {
            data[NotificationDataKeys.PetId] = pet.ToString();
        }
    }

    /// <summary>
    /// Only set the key when there is a real name — an empty value would render
    /// as a blank gap, whereas an absent one lets the renderer substitute its
    /// "your pet" fallback.
    /// </summary>
    private static void AddPetName(IDictionary<string, string> data, string? petName)
    {
        if (!string.IsNullOrWhiteSpace(petName))
        {
            data[NotificationDataKeys.PetName] = petName.Trim();
        }
    }
}

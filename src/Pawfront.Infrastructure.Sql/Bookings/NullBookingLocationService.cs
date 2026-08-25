using Microsoft.Extensions.Logging;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory dev fallback for <see cref="IBookingLocationService"/>: validates the
/// fix exactly as the real one does, then logs and drops it.
/// <para>
/// Accepting rather than throwing is deliberate. This is not a feature a developer
/// can meaningfully exercise without SQL — there is no in-memory location table and
/// no app-facing read to satisfy — but the surrounding flows (marking a no-show,
/// recording a payment) MUST stay usable on a machine with no database, and every
/// one of them now carries a location. Throwing here would take those flows down
/// with it. The half of the rule that matters at the API — a request without a
/// usable fix is refused — is enforced in the Application layer and so still holds.
/// Same posture as <c>NullNotificationPublisher</c>.
/// </para>
/// </summary>
internal sealed class NullBookingLocationService(ILogger<NullBookingLocationService> logger)
    : IBookingLocationService
{
    public Task<BookingLocationEventResult> RecordAsync(
        RecordBookingLocationCommand command,
        CancellationToken cancellationToken)
    {
        var trigger = BookingLocationTriggers.NormalizeRecordable(command.Trigger);
        var location = CapturedLocation.Require(command.Location, "record your location");

        logger.LogInformation(
            "In-memory store: dropping {Trigger} location for {BookingType} booking {BookingId} by {Actor}.",
            trigger, command.BookingType, command.BookingId, command.Actor);

        return Task.FromResult(new BookingLocationEventResult(
            Guid.NewGuid(),
            command.BookingId,
            trigger,
            command.Actor.ToString(),
            command.ActorId,
            location.Latitude,
            location.Longitude,
            location.AccuracyMetres,
            location.DeviceCapturedAtUtc,
            DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<BookingLocationEventResult>> ListAsync(
        string bookingType,
        Guid bookingId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BookingLocationEventResult>>([]);
}

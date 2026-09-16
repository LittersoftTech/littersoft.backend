namespace Pawfront.Application.Bookings;

/// <summary>
/// One geolocation fix supplied by a client, for the booking moments that have to
/// be evidenced. Every field but the coordinate pair is optional; the pair is not.
/// <para>
/// Coordinates are the device's own reading, taken at the moment of the action —
/// the server never derives one party's position from the other's, nor reuses an
/// earlier fix. A moment with no row against it means nobody's device reported one,
/// which is a truthful answer; a stale coordinate presented as a live one would not
/// be.
/// </para>
/// </summary>
/// <param name="AccuracyMetres">
/// The device's own reported horizontal accuracy. Worth carrying because a fix good
/// to 5 m and one good to 3 km support very different claims about whether somebody
/// was at an address.
/// </param>
/// <param name="DeviceCapturedAtUtc">
/// When the DEVICE took the reading. Untrusted — it is the client's clock, and the
/// server stamps its own <c>RecordedAtUtc</c> regardless. A wide gap between the two
/// is itself a signal that a cached fix was replayed.
/// </param>
public sealed record CapturedLocation(
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMetres = null,
    DateTimeOffset? DeviceCapturedAtUtc = null)
{
    /// <summary>
    /// Range-checks the fix, mirroring the CHECK constraints on
    /// <c>Booking.BookingLocationEvents</c> so a bad value comes back as a typed
    /// 400 rather than a constraint violation at the end of a transaction.
    /// </summary>
    /// <exception cref="InvalidCapturedLocationException"/>
    public void Validate()
    {
        if (Latitude is < -90m or > 90m)
        {
            throw new InvalidCapturedLocationException(
                $"Latitude must be between -90 and 90; got {Latitude}.");
        }

        if (Longitude is < -180m or > 180m)
        {
            throw new InvalidCapturedLocationException(
                $"Longitude must be between -180 and 180; got {Longitude}.");
        }

        if (AccuracyMetres is < 0m)
        {
            throw new InvalidCapturedLocationException(
                $"Accuracy must not be negative; got {AccuracyMetres}.");
        }
    }

    /// <summary>
    /// Validates a fix that is REQUIRED for the action being performed. Every one
    /// of the evidenced moments uses this: the action is refused outright when the
    /// device could not supply a position, rather than proceeding with a gap in the
    /// record.
    /// </summary>
    /// <exception cref="MissingCapturedLocationException"/>
    /// <exception cref="InvalidCapturedLocationException"/>
    public static CapturedLocation Require(CapturedLocation? location, string action)
    {
        if (location is null)
        {
            throw new MissingCapturedLocationException(action);
        }

        location.Validate();
        return location;
    }
}

/// <summary>
/// The moments a booking's geolocation is captured at. Seven values, covering the
/// eight rows of the product spec — the two arrival questions ("have you arrived at
/// the customer's location?" and "has the customer arrived?") collapse into
/// <see cref="ArrivalConfirmed"/>, because which one the app asked follows entirely
/// from the booking's own <c>LocationType</c>, and deriving it there means a client
/// cannot misreport which question it put on screen.
/// </summary>
public static class BookingLocationTriggers
{
    /// <summary>Provider answered the arrival question (rows 1 and 2). Provider-side only.</summary>
    public const string ArrivalConfirmed = "ArrivalConfirmed";

    /// <summary>Provider tapped "Proceed to start" — recorded only when the job actually starts. Provider-side only.</summary>
    public const string JobStartProceeded = "JobStartProceeded";

    /// <summary>Either party marked the other absent. Both sides capture.</summary>
    public const string NoShowMarked = "NoShowMarked";

    /// <summary>Parent opened their start code. Parent-side only.</summary>
    public const string StartOtpShown = "StartOtpShown";

    /// <summary>Provider swiped "Cash Received". Both sides capture.</summary>
    public const string CashReceived = "CashReceived";

    /// <summary>Provider marked "Cash Not Received". Both sides capture. Log only — see <see cref="IBookingLocationService"/>.</summary>
    public const string CashNotReceived = "CashNotReceived";

    /// <summary>Provider took a completion photo. Both sides capture; repeats once per photo.</summary>
    public const string EvidenceCaptured = "EvidenceCaptured";

    /// <summary>
    /// The triggers a client may name on the standalone record endpoint. The other
    /// three are written by the transition that performs them and can never be
    /// posted directly — a client that could assert "the job started here" without
    /// the job having started would defeat the point of capturing it.
    /// </summary>
    public static readonly IReadOnlySet<string> Recordable = new HashSet<string>(StringComparer.Ordinal)
    {
        NoShowMarked, CashReceived, CashNotReceived, EvidenceCaptured
    };

    /// <summary>
    /// Matches <paramref name="value"/> case-insensitively onto the canonical
    /// casing, restricted to <see cref="Recordable"/>.
    /// </summary>
    /// <exception cref="UnsupportedBookingLocationTriggerException"/>
    public static string NormalizeRecordable(string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var candidate in Recordable)
            {
                if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        throw new UnsupportedBookingLocationTriggerException(trimmed ?? string.Empty);
    }
}

/// <summary>
/// A party records their own position for a moment the COUNTERPARTY drove (or, for
/// <see cref="BookingLocationTriggers.CashNotReceived"/>, for a moment that changes
/// no status at all). <paramref name="ActorId"/> comes from the authenticated route,
/// never the body, and is checked against the booking.
/// </summary>
public sealed record RecordBookingLocationCommand(
    string BookingType,
    Guid BookingId,
    string Trigger,
    BookingStatusActor Actor,
    Guid ActorId,
    CapturedLocation? Location);

/// <summary>One recorded geolocation fix, as read back after it is stored.</summary>
public sealed record BookingLocationEventResult(
    Guid BookingLocationEventId,
    Guid BookingId,
    string Trigger,
    string CapturedByType,
    Guid CapturedById,
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMetres,
    DateTimeOffset? DeviceCapturedAtUtc,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Writes the geolocation fixes that do NOT ride along inside a transition sproc:
/// a party's own position for a moment the counterparty drove, and the log-only
/// "Cash Not Received".
/// <para>
/// One service for both booking kinds — <see cref="RecordBookingLocationCommand.BookingType"/>
/// selects the table — for the same reason <see cref="IBulkBookingCancellationService"/>
/// is: the caller's screen mixes them.
/// </para>
/// <para>
/// <b>"Cash Not Received" changes nothing.</b> It records that the provider raised
/// the payment prompt and was not paid, with both parties' positions, and leaves the
/// booking COMPLETED with its payout still <c>Pending</c>. It is deliberately not a
/// new payout outcome: a cancelled or unpaid booking has a cancellation policy
/// attached and the refund / chase leg is not built, so freezing a verdict on the
/// money here would prejudge it.
/// </para>
/// </summary>
public interface IBookingLocationService
{
    /// <summary>
    /// Records one fix. Throws <see cref="BookingNotFoundException"/> /
    /// <see cref="NightStayBookingNotFoundException"/> (unknown booking),
    /// <see cref="BookingStatusForbiddenException"/> (not a party),
    /// <see cref="UnsupportedBookingLocationTriggerException"/>, or
    /// <see cref="InvalidCapturedLocationException"/>.
    /// </summary>
    Task<BookingLocationEventResult> RecordAsync(
        RecordBookingLocationCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// The full location timeline for one booking, oldest first — the read a
    /// support/admin screen needs when working a dispute. Empty for an unknown
    /// booking (list semantics, no exception).
    /// <para>
    /// Deliberately NOT exposed on either app: these are both parties' precise
    /// coordinates, and handing a provider the parent's position (or the reverse)
    /// is a safety problem. The stated purpose of the capture is to confirm things
    /// to Pawfront, not to the counterparty.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BookingLocationEventResult>> ListAsync(
        string bookingType,
        Guid bookingId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The action requires a geolocation and none was supplied. Every one of the
/// evidenced moments is gated this way: without a position the action is refused
/// rather than recorded with a gap.
/// </summary>
public sealed class MissingCapturedLocationException(string action)
    : Exception($"A geolocation is required to {action}. Enable location access and try again.")
{
    /// <summary>The action that was refused, for the message the client shows.</summary>
    public string Action { get; } = action;
}

/// <summary>The supplied coordinate is out of range (or the accuracy is negative).</summary>
public sealed class InvalidCapturedLocationException(string message) : Exception(message);

/// <summary>The supplied trigger is not one a client may record directly.</summary>
public sealed class UnsupportedBookingLocationTriggerException(string value)
    : Exception(
        $"Location trigger '{value}' is not supported. Use one of: "
        + $"{string.Join(", ", BookingLocationTriggers.Recordable)}.");

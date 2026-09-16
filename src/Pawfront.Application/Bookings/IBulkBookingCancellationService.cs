namespace Pawfront.Application.Bookings;

/// <summary>
/// Which booking table an id points at. The two kinds live in separate tables
/// (<c>Booking.Bookings</c> / <c>Booking.NightStayBookings</c>) and share no id
/// space, so a bare GUID cannot say which one it is — every batch item carries
/// its type alongside its id. Same two values <c>Booking.BookingPayments.BookingType</c>
/// and <c>Review.BookingReviews.BookingType</c> store (and that
/// <see cref="Reviews.ReviewedBookingTypes"/> names for the reviews flow).
/// </summary>
public static class BookingTypes
{
    public const string SingleDay = "SingleDay";
    public const string NightStay = "NightStay";

    /// <summary>
    /// Matches <paramref name="value"/> case-insensitively onto the canonical
    /// casing. Null when it is blank or unrecognised — callers report that as a
    /// per-item failure rather than throwing, so one mistyped entry in a batch
    /// doesn't sink the rest of it.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (string.Equals(trimmed, SingleDay, StringComparison.OrdinalIgnoreCase))
        {
            return SingleDay;
        }

        return string.Equals(trimmed, NightStay, StringComparison.OrdinalIgnoreCase)
            ? NightStay
            : null;
    }
}

/// <summary>The bounds a bulk cancellation is built to.</summary>
public static class BulkBookingCancellationLimits
{
    /// <summary>
    /// Bookings per request. Each item is its own transaction against the status
    /// engine (and its own push to the counterparty), so an unbounded batch would
    /// be an unbounded request — the cap keeps one call's cost predictable. Well
    /// above any plausible multi-select on a "my bookings" screen.
    /// </summary>
    public const int MaxItems = 50;
}

/// <summary>
/// Per-item failure codes. These deliberately reuse the exact codes the
/// single-booking cancel endpoints return in their <c>error.code</c>, so a client
/// learns one vocabulary whether it cancels one booking or twenty. The set covers
/// what a <i>cancel</i> transition can raise — it is not a mirror of the endpoints'
/// full booking-error switch, most of which belongs to create/modify/start paths a
/// cancellation never touches.
/// </summary>
public static class BulkCancelErrorCodes
{
    /// <summary>Unknown single-day booking id.</summary>
    public const string BookingNotFound = "BookingNotFound";

    /// <summary>Unknown night-stay booking id.</summary>
    public const string NightStayBookingNotFound = "NightStayBookingNotFound";

    /// <summary>The caller is not a party to this booking.</summary>
    public const string Forbidden = "Forbidden";

    /// <summary>Already cancelled, declined, completed, paid, expired or no-showed.</summary>
    public const string BookingStatusTerminal = "BookingStatusTerminal";

    /// <summary>Already in the cancelled status this actor sets.</summary>
    public const string BookingStatusUnchanged = "BookingStatusUnchanged";

    /// <summary>The job is underway (IN_PROGRESS) — it runs through to completion.</summary>
    public const string BookingInProgress = "BookingInProgress";

    /// <summary>Nobody accepted the booking in time; it is already effectively dead.</summary>
    public const string BookingExpired = "BookingExpired";

    /// <summary>Defensive — the status engine refused the status for this actor.</summary>
    public const string BookingStatusNotAllowed = "BookingStatusNotAllowed";

    /// <summary>Defensive — the pinned cancel status was not recognised.</summary>
    public const string UnsupportedBookingStatus = "UnsupportedBookingStatus";

    /// <summary>The item's <c>bookingType</c> was blank or not one of the two kinds.</summary>
    public const string UnsupportedBookingType = "UnsupportedBookingType";

    /// <summary>Anything else the transition rejected the item for.</summary>
    public const string InvalidRequest = "InvalidRequest";
}

/// <summary>
/// One booking to cancel. <paramref name="BookingType"/> is validated per item
/// (see <see cref="BookingTypes.Normalize"/>) rather than up front, so a bad value
/// fails only its own entry.
/// </summary>
public sealed record BulkCancelBookingItem(Guid BookingId, string? BookingType);

/// <summary>
/// A batch cancellation. The acting party and its id come from the authenticated
/// route, never from the request body — exactly as the single-booking transitions
/// do — and the target status follows from <paramref name="Actor"/>:
/// PROVIDER_CANCELLED for a provider, PARENT_CANCELLED for a parent.
/// </summary>
public sealed record BulkCancelBookingsCommand(
    IReadOnlyList<BulkCancelBookingItem> Items,
    BookingStatusActor Actor,
    Guid ActorId,
    // Optional free text recorded on each booking's audit row, e.g. "cancelled
    // from the app's multi-select". One note covers the whole batch.
    string? Note);

/// <summary>
/// What happened to one booking. Exactly one side is populated: a cancelled
/// booking carries <see cref="Status"/> + <see cref="CancelledAtUtc"/> and no
/// error; a refused one carries <see cref="ErrorCode"/> + <see cref="Message"/>.
/// </summary>
/// <remarks>
/// A refusal deliberately does NOT report the booking's current status — the
/// transition threw rather than returning a row, and re-reading each failed
/// booking just to fill the field would cost a round trip per item for something
/// the client gets by refreshing its list.
/// </remarks>
public sealed record BulkCancelBookingOutcome(
    Guid BookingId,
    string BookingType,
    bool Cancelled,
    string? Status,
    DateTimeOffset? CancelledAtUtc,
    string? ErrorCode,
    string? Message);

/// <summary>
/// The batch's outcome. <see cref="CancelledCount"/> + <see cref="FailedCount"/>
/// always equals <see cref="RequestedCount"/>, which equals
/// <see cref="Results"/>.Count.
/// </summary>
/// <param name="RequestedCount">
/// Distinct bookings processed — after duplicate (id, type) pairs in the request
/// are collapsed, since the same booking cancelled twice is one cancellation.
/// </param>
public sealed record BulkCancelBookingsResult(
    int RequestedCount,
    int CancelledCount,
    int FailedCount,
    IReadOnlyList<BulkCancelBookingOutcome> Results);

/// <summary>
/// Cancels several bookings of either kind in one call, on behalf of one party.
/// </summary>
/// <remarks>
/// <para>
/// Every item goes through the ordinary per-booking transition
/// (<c>Booking.UpdateBookingStatus</c> / <c>Booking.UpdateNightStayBookingStatus</c>),
/// so a bulk cancellation is indistinguishable from N single cancellations: the
/// same party check, the same terminal / job-underway guards, the same audit row,
/// the same capacity release, and the same push to the counterparty. There is no
/// bulk SQL path and no new stored procedure — a batch is a loop, deliberately,
/// because that is what makes the two surfaces impossible to drift apart.
/// </para>
/// <para>
/// <b>Per-item, not all-or-nothing.</b> Each booking is its own transaction and
/// each cancellation stands on its own, so one refusal does not roll back the
/// others: a multi-select containing a booking that is already cancelled should
/// still cancel the rest rather than silently doing nothing. Callers read
/// <see cref="BulkCancelBookingsResult.Results"/> to see which is which.
/// </para>
/// </remarks>
public interface IBulkBookingCancellationService
{
    /// <summary>
    /// Cancels each booking in the batch. Throws <see cref="ArgumentException"/>
    /// only for a malformed <i>batch</i> (empty, or over
    /// <see cref="BulkBookingCancellationLimits.MaxItems"/>); anything wrong with
    /// an individual booking is reported as that item's outcome.
    /// </summary>
    Task<BulkCancelBookingsResult> CancelAsync(
        BulkCancelBookingsCommand command,
        CancellationToken cancellationToken);
}

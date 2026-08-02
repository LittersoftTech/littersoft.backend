namespace Pawfront.Application.Bookings;

/// <summary>
/// Compares a booking's frozen-at-creation terms against the provider's CURRENT
/// ones, so the app can show a "these things changed since you booked" sheet
/// before either party edits the schedule.
///
/// A booking snapshots its price, cancellation policy, selected-location address
/// (and, for a stay, drop-off / pick-up times) at creation, and every read prefers
/// the snapshot — that's what protects an existing booking from a later provider
/// edit. This service surfaces the difference the snapshot is hiding. Nothing here
/// mutates a booking: the drifted values only land when a modification is
/// requested WITH acknowledgement and the counterparty accepts it.
/// </summary>
public interface IBookingTermsChangeService
{
    /// <summary>
    /// Drift on a single-day booking. Throws
    /// <see cref="BookingNotFoundException"/> when the booking doesn't exist.
    /// </summary>
    Task<BookingTermsChangeResult> GetForBookingAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Drift on a multi-night stay. Throws
    /// <see cref="NightStayBookingNotFoundException"/> when the stay doesn't exist.
    /// </summary>
    Task<BookingTermsChangeResult> GetForNightStayBookingAsync(Guid bookingId, CancellationToken cancellationToken);
}

/// <summary>
/// The drift on one booking. <see cref="Changes"/> is empty (and
/// <see cref="HasChanges"/> false) when the provider's terms still match what the
/// booking froze — the ordinary case, where the edit screen shows no sheet.
///
/// <see cref="AcknowledgedTerms"/> is the COMPLETE current term set to stage on a
/// modification request: each field is the live value when it could be resolved,
/// falling back to the booking's own frozen value otherwise. Staging a complete
/// set is what lets the accept-side SQL apply it verbatim. It is Application-only
/// — the wire contract exposes just the change list.
/// </summary>
public sealed record BookingTermsChangeResult(
    Guid BookingId,
    bool HasChanges,
    IReadOnlyList<BookingTermsChangeItem> Changes,
    BookingAcknowledgedTerms AcknowledgedTerms);

/// <summary>
/// One drifted term, ready to render as a row in the confirmation sheet.
/// <see cref="BookedValue"/> / <see cref="CurrentValue"/> are display strings
/// (invariant-formatted) because the fields are heterogeneous — money, hours,
/// clock times, a postal address.
/// </summary>
/// <param name="Field">
/// Stable machine key: <c>Price</c>, <c>CancellationPolicy</c>, <c>DropOffTime</c>,
/// <c>PickUpTime</c>, <c>Location</c>, <c>Duration</c>, <c>MinimumDuration</c>,
/// <c>MinimumNights</c>. See <see cref="BookingTermsChangeFields"/>.
/// </param>
/// <param name="ChangeType">
/// <c>ValueChanged</c> — a frozen term now differs from the live one, and
/// accepting the modification adopts the new value. <c>RuleViolation</c> — the
/// booking's own window no longer satisfies a rule the provider has since changed
/// (e.g. the session is now a fixed 90 minutes); there is no old-vs-new value to
/// adopt, so the requester must pick a conforming window. See
/// <see cref="BookingTermsChangeTypes"/>.
/// </param>
public sealed record BookingTermsChangeItem(
    string Field,
    string ChangeType,
    string? BookedValue,
    string? CurrentValue,
    string Message);

/// <summary>
/// The complete current term set staged with a modification proposal and applied
/// to the booking when the counterparty accepts. Drop-off / pick-up are night-stay
/// only (null on a single-day booking).
/// </summary>
public sealed record BookingAcknowledgedTerms(
    decimal? UnitPrice,
    int? CancellationPolicyHours,
    TimeOnly? DropOffTime,
    TimeOnly? PickUpTime,
    string? AddressLine,
    string? City,
    string? ZipCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>Machine keys for <see cref="BookingTermsChangeItem.Field"/>.</summary>
public static class BookingTermsChangeFields
{
    public const string Price = "Price";
    public const string CancellationPolicy = "CancellationPolicy";
    public const string DropOffTime = "DropOffTime";
    public const string PickUpTime = "PickUpTime";
    public const string Location = "Location";
    public const string Duration = "Duration";
    public const string MinimumDuration = "MinimumDuration";
    public const string MinimumNights = "MinimumNights";
}

/// <summary>Machine keys for <see cref="BookingTermsChangeItem.ChangeType"/>.</summary>
public static class BookingTermsChangeTypes
{
    /// <summary>A frozen term now differs from the provider's live one.</summary>
    public const string ValueChanged = "ValueChanged";

    /// <summary>The booked window no longer satisfies a changed duration rule.</summary>
    public const string RuleViolation = "RuleViolation";
}

/// <summary>
/// The provider's terms have changed since the booking was created and the caller
/// submitted a modification without acknowledging them. Maps to
/// <c>409 BookingTermsChanged</c>; the client reads the drift from the
/// terms-changes endpoint, confirms with the user, and resubmits with
/// <c>acknowledgeTermsChanges: true</c>.
/// </summary>
public sealed class BookingTermsChangedException(Guid bookingId)
    : Exception($"The provider's terms for booking '{bookingId}' have changed since it was created. "
                + "Review the changes and resubmit with acknowledgeTermsChanges set.");

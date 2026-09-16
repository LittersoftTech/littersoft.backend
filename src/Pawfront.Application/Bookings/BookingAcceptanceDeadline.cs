namespace Pawfront.Application.Bookings;

/// <summary>
/// The last moment a provider can still accept a booking before it is removed.
///
/// TWO independent rules expire an unaccepted booking, and the deadline is
/// whichever fires FIRST:
/// <list type="bullet">
///   <item><b>BR-17</b> — it has sat in <c>CREATED</c> for 24 hours.</item>
///   <item><b>BR-53</b> — the service is now less than
///     <see cref="BookingLeadTime.Minimum"/> away, however recently it was made.</item>
/// </list>
///
/// Taking BR-17 alone would be wrong in a common case: a booking made at 10:00
/// for a 14:00 service the same day is removed by BR-53 at 12:00, not at 10:00
/// the next morning. Anything shown to a provider must agree with what
/// <c>Booking.ExpireStaleCreatedBookings</c> will actually do.
///
/// Both rules are evaluated in UTC, like every other time in this codebase.
/// </summary>
public static class BookingAcceptanceDeadline
{
    /// <summary>
    /// How long a booking may sit unaccepted (BR-17). Mirrors the
    /// <c>@PendingHours</c> default on <c>Booking.ExpireStaleCreatedBookings</c> —
    /// change one, change the other.
    /// </summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// The accept-by instant for a booking created at <paramref name="createdAtUtc"/>
    /// whose service begins at <paramref name="serviceStartUtc"/>.
    /// </summary>
    public static DateTimeOffset Compute(DateTimeOffset createdAtUtc, DateTimeOffset serviceStartUtc)
    {
        var pendingExpiry = createdAtUtc + PendingWindow;      // BR-17
        var leadTimeExpiry = serviceStartUtc - BookingLeadTime.Minimum; // BR-53

        return pendingExpiry <= leadTimeExpiry ? pendingExpiry : leadTimeExpiry;
    }

    /// <inheritdoc cref="Compute(DateTimeOffset, DateTimeOffset)"/>
    /// <remarks>
    /// Overload taking the service date + start time directly — the same
    /// <c>BookingDate + StartTime</c> / <c>CheckInDate + DropOffTime</c>
    /// arithmetic <see cref="BookingLeadTime.ServiceStartUtc"/> does.
    /// </remarks>
    public static DateTimeOffset Compute(
        DateTimeOffset createdAtUtc,
        DateOnly serviceDate,
        TimeOnly startTime)
        => Compute(createdAtUtc, BookingLeadTime.ServiceStartUtc(serviceDate, startTime));
}

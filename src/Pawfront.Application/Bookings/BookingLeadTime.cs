namespace Pawfront.Application.Bookings;

/// <summary>
/// How far ahead of the service a booking has to be made. A parent browsing at
/// 11:00 sees 13:00 as the first bookable slot, however open the provider's day
/// is — the gap gives the provider time to see and accept the job.
/// <para>
/// The cutoff is derived from the SAME instant the modification window
/// uses — <c>serviceStart</c>, i.e. <c>BookingDate + StartTime</c> for a
/// single-day booking and <c>CheckInDate + DropOffTime</c> for a stay — so the
/// two rules bracket a booking's life symmetrically: it can be created up to
/// <see cref="Minimum"/> before the service, and changed up to the same cutoff.
/// All UTC, per the codebase convention.
/// </para>
/// <para>
/// Enforced in the Application layer, alongside the working-hours and closure
/// gates rather than in SQL: it is not a race (a window that clears the cutoff
/// when validated still clears it a few milliseconds later in the sproc), so the
/// race-safe SQL guard that capacity needs has no counterpart here.
/// </para>
/// </summary>
public static class BookingLeadTime
{
    /// <summary>The minimum gap between "now" and the start of the service.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromHours(2);

    /// <summary>
    /// The instant a booking's service begins, read as UTC — the same
    /// <c>BookingDate + StartTime</c> / <c>CheckInDate + DropOffTime</c>
    /// arithmetic the modification-window sprocs do in SQL.
    /// </summary>
    public static DateTimeOffset ServiceStartUtc(DateOnly serviceDate, TimeOnly startTime)
        => new(serviceDate.ToDateTime(startTime), TimeSpan.Zero);

    /// <summary>The earliest service start a booking made at <paramref name="nowUtc"/> may have.</summary>
    public static DateTimeOffset EarliestBookableStart(DateTimeOffset nowUtc)
        => nowUtc + Minimum;

    /// <summary>True when the service starts too soon to be booked now.</summary>
    public static bool IsTooSoon(DateTimeOffset serviceStartUtc, DateTimeOffset nowUtc)
        => serviceStartUtc < EarliestBookableStart(nowUtc);

    /// <inheritdoc cref="IsTooSoon(DateTimeOffset, DateTimeOffset)"/>
    public static bool IsTooSoon(DateOnly serviceDate, TimeOnly startTime, DateTimeOffset nowUtc)
        => IsTooSoon(ServiceStartUtc(serviceDate, startTime), nowUtc);

    /// <summary>
    /// Throws when the requested service start is inside the lead-time window.
    /// Shared by the single-day and night-stay create paths.
    /// </summary>
    public static void EnsureFarEnoughAhead(DateOnly serviceDate, TimeOnly startTime, DateTimeOffset nowUtc)
    {
        if (IsTooSoon(serviceDate, startTime, nowUtc))
        {
            throw new BookingLeadTimeTooShortException(EarliestBookableStart(nowUtc));
        }
    }
}

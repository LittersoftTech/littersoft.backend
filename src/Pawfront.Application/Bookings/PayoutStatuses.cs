namespace Pawfront.Application.Bookings;

/// <summary>
/// The <c>PayoutStatus</c> vocabulary carried on <c>Booking.Bookings</c> and
/// <c>Booking.NightStayBookings</c> (and mirrored by the two CHECK constraints).
/// SQL owns the values — every write happens in a stored procedure — so these
/// constants exist to name them for C# readers and for the in-memory dev stores,
/// which have no database to read the column from.
/// </summary>
public static class PayoutStatuses
{
    /// <summary>Default at insert: the money has not moved yet.</summary>
    public const string Pending = "Pending";

    /// <summary>Reserved for a future payout-execution leg; nothing writes it.</summary>
    public const string Processing = "Processing";

    /// <summary>The provider recorded the payment (booking reached PAID).</summary>
    public const string Paid = "Paid";

    /// <summary>Reserved for a future payout-execution leg; nothing writes it.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// TERMINAL: no money can ever move on this booking. Written when the job
    /// ends as a no-show or expires unaccepted — nobody performed and nobody
    /// owes, so leaving it 'Pending' would claim a payout is on its way that
    /// never can be. Deliberately not a stage in the pipeline: clients render it
    /// as "No Payout" and stop.
    /// </summary>
    public const string NoPayout = "NO_PAYOUT";

    /// <summary>
    /// The payout status implied by a booking's lifecycle status. This is what
    /// the transition stored procedures write, restated for the in-memory dev
    /// stores; SQL remains the source of truth for real rows.
    /// </summary>
    public static string ForBookingStatus(string bookingStatus) => bookingStatus switch
    {
        BookingStatuses.Paid => Paid,
        BookingStatuses.ParentNoShow or BookingStatuses.ProviderNoShow or BookingStatuses.Expired => NoPayout,
        _ => Pending
    };
}

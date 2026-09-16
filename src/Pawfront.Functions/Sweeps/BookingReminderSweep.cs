using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>
/// Per-arm counts from one reminder pass, for the function's log line. These are
/// notifications ENQUEUED (before outbox de-duplication), not devices reached.
/// </summary>
internal sealed record BookingReminderSweepResult(
    int DayBeforeReminders,
    int StartingSoonReminders,
    int NotStartedNudges,
    int StartOtpNudges,
    int PickupNudges)
{
    public bool DidWork =>
        DayBeforeReminders > 0 || StartingSoonReminders > 0 || NotStartedNudges > 0
        || StartOtpNudges > 0 || PickupNudges > 0;
}

/// <summary>
/// The time-driven booking reminders and nudges (V3 spec cards P-S4..P-S7, P-S9,
/// P-S10, P-S13, P-S14 and V-S5..V-S8, V-S10, V-S12).
///
/// Unlike the three settlement sweeps this changes no booking status — it only
/// enqueues notifications — which is why it can safely run every minute.
/// </summary>
internal static class BookingReminderSweep
{
    public static async Task<BookingReminderSweepResult> RunAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Booking.SendBookingReminders", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new BookingReminderSweepResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }
}

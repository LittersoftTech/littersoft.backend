using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>
/// A booking nobody accepted has expired, on either trigger the sproc applies:
/// BR-17 (stuck in CREATED for 24+ hours) or BR-53 (still in CREATED with under
/// 2 hours to the service). The counts are totals across both; which trigger
/// fired is recorded per booking in the status-history note.
/// </summary>
internal sealed record StaleCreatedSweepResult(int ExpiredBookings, int ExpiredNightStayBookings);

internal static class StaleCreatedBookingSweep
{
    public static async Task<StaleCreatedSweepResult> RunAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Booking.ExpireStaleCreatedBookings", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new StaleCreatedSweepResult(reader.GetInt32(0), reader.GetInt32(1));
    }
}

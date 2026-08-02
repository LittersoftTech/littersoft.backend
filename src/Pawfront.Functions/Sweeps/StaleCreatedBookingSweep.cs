using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>BR-17: a booking stuck in CREATED for 24+ hours has expired.</summary>
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

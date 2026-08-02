using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>
/// BR-38: an accepted job still unstarted once the provider's working day ends
/// settles as a no-show (single-day), or once the check-in day ends (night-stay).
/// </summary>
internal sealed record NoShowSweepResult(
    int ProviderNoShowBookings,
    int ParentNoShowBookings,
    int ProviderNoShowNightStayBookings,
    int ParentNoShowNightStayBookings);

internal static class UnstartedJobNoShowSweep
{
    public static async Task<NoShowSweepResult> RunAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Booking.SettleUnstartedJobsAsNoShow", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new NoShowSweepResult(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }
}

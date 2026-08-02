using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>
/// BR-30: an unanswered modification proposal — from either party (widened
/// 2026-08-02; previously parent-only) — past its 2-hour cutoff reverts to
/// CONFIRMED.
/// </summary>
internal sealed record ExpiredModificationSweepResult(int RevertedBookings, int RevertedNightStayBookings);

internal static class ExpiredModificationRequestSweep
{
    public static async Task<ExpiredModificationSweepResult> RunAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Booking.RevertExpiredModificationRequests", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new ExpiredModificationSweepResult(reader.GetInt32(0), reader.GetInt32(1));
    }
}

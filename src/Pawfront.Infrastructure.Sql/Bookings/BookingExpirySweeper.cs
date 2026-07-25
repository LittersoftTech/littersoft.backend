using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// Background sweeper that expires bookings in two ways: every
/// <see cref="SweepInterval"/> it runs <c>Booking.ExpireStaleBookings</c>,
/// which flips single-day AND night-stay bookings (a) still in CREATED 24+
/// hours after creation to the terminal EXPIRED status (provider never
/// accepted), and (b) accepted-but-never-started with an elapsed scheduled
/// window to the terminal JOB_EXPIRED status — both freeing capacity, each
/// with a 'System' audit row. The status-engine sprocs apply the EXPIRED flip
/// lazily too (THROW 51129/51249), so an accept landing between sweeps is
/// still rejected; this service keeps the stored state fresh so reads show the
/// terminal status without waiting for a transition attempt. Registered in
/// both API hosts; the sproc is idempotent and race-safe, so overlapping
/// sweeps are harmless.
/// </summary>
internal sealed class BookingExpirySweeper(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    ILogger<BookingExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private const int PendingHours = 24;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Transient SQL trouble must not kill the host — log and retry
                // at the next tick (the lazy in-sproc guard still protects the
                // accept path in the meantime).
                logger.LogError(exception, "Booking expiry sweep failed; retrying at the next interval.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ExpireStaleBookings", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PendingHours", PendingHours);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var expiredBookings = reader.GetInt32(0);
            var expiredNightStays = reader.GetInt32(1);
            var jobExpiredBookings = reader.GetInt32(2);
            var jobExpiredNightStays = reader.GetInt32(3);
            if (expiredBookings > 0 || expiredNightStays > 0
                || jobExpiredBookings > 0 || jobExpiredNightStays > 0)
            {
                logger.LogInformation(
                    "Expired {ExpiredBookings} booking(s) and {ExpiredNightStayBookings} night-stay booking(s) pending for {PendingHours}+ hours; "
                    + "job-expired {JobExpiredBookings} booking(s) and {JobExpiredNightStayBookings} night-stay booking(s) accepted but never started.",
                    expiredBookings, expiredNightStays, PendingHours, jobExpiredBookings, jobExpiredNightStays);
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}

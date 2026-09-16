using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Functions.Sweeps;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Runs the time-triggered booking rules every 5 minutes, in order — replaces
/// the retired in-database <c>Booking.ExpireStaleBookings</c> sweep
/// (see docs/booking-rules.md BR-17 / BR-53 / BR-30 / BR-38). Unlike the retired
/// in-process <c>BackgroundService</c> — which ran in BOTH API hosts with no
/// coordination between them — a timer-triggered Function has its invocations
/// serialised by the Functions host itself, so exactly one instance runs each
/// tick even if this app scales out. That guarantee only holds if this stays the
/// ONE deployed Function App for this trigger.
/// </summary>
public sealed class BookingSweepFunction(
    IConfiguration configuration,
    IPawfrontSecretProvider secretProvider,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<BookingSweepFunction>();

    [Function("BookingSweepFunction")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // A transient SQL failure must not stop future ticks — the Functions
            // host schedules the next invocation regardless of this one's outcome.
            _logger.LogError(exception, "Booking sweep failed; will retry at the next scheduled tick.");
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // BR-17 + BR-53 first (they free an unaccepted CREATED booking
        // outright), then BR-30 BEFORE BR-38 — a booking whose modification
        // proposal expires AND whose provider's working day has also ended
        // settles as a no-show in this same tick rather than waiting for the
        // next one.
        var expired = await StaleCreatedBookingSweep.RunAsync(connection, cancellationToken);
        var reverted = await ExpiredModificationRequestSweep.RunAsync(connection, cancellationToken);
        var noShows = await UnstartedJobNoShowSweep.RunAsync(connection, cancellationToken);

        if (expired.ExpiredBookings > 0 || expired.ExpiredNightStayBookings > 0)
        {
            _logger.LogInformation(
                "BR-17/BR-53: expired {ExpiredBookings} booking(s) and {ExpiredNightStayBookings} night-stay " +
                "booking(s) that were never accepted — pending for 24+ hours, or with under 2 hours left " +
                "before the service starts.",
                expired.ExpiredBookings, expired.ExpiredNightStayBookings);
        }

        if (reverted.RevertedBookings > 0 || reverted.RevertedNightStayBookings > 0)
        {
            _logger.LogInformation(
                "BR-30: reverted {RevertedBookings} booking(s) and {RevertedNightStayBookings} night-stay " +
                "booking(s) to CONFIRMED after an unanswered modification request expired.",
                reverted.RevertedBookings, reverted.RevertedNightStayBookings);
        }

        if (noShows.ProviderNoShowBookings > 0 || noShows.ParentNoShowBookings > 0
            || noShows.ProviderNoShowNightStayBookings > 0 || noShows.ParentNoShowNightStayBookings > 0)
        {
            _logger.LogInformation(
                "BR-38: marked {ProviderNoShowBookings} booking(s) PROVIDER_NO_SHOW and {ParentNoShowBookings} " +
                "PARENT_NO_SHOW after the provider's working day ended with the job unstarted; marked " +
                "{ProviderNoShowNightStayBookings} night-stay booking(s) PROVIDER_NO_SHOW and " +
                "{ParentNoShowNightStayBookings} PARENT_NO_SHOW after the check-in day ended with the stay unstarted.",
                noShows.ProviderNoShowBookings, noShows.ParentNoShowBookings,
                noShows.ProviderNoShowNightStayBookings, noShows.ParentNoShowNightStayBookings);
        }
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        var configured = configuration.GetConnectionString("SqlServer");
        return string.IsNullOrWhiteSpace(configured)
            ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken)
            : configured;
    }
}

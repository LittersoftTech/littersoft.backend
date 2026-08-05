using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Functions.Sweeps;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Enqueues the time-driven booking reminders and nudges every minute.
///
/// <b>Separate from <see cref="BookingSweepFunction"/> on purpose, and on a
/// different cadence.</b> That one settles booking STATUS (expiry, no-show,
/// modification revert) and runs every 5 minutes, which is ample for rules
/// measured in hours. This one only enqueues notifications and changes nothing,
/// so a tighter cadence carries no lifecycle risk — and it needs one: the
/// "starts in 5 minutes" reminder would otherwise land anywhere from 0 to 5
/// minutes before the service, which is precisely the claim it must not get
/// wrong.
///
/// Re-firing is prevented by the outbox's filtered UNIQUE DedupeKey rather than
/// by state on the booking — see Booking.SendBookingReminders for why. That means
/// a missed tick is self-healing (the next one picks the booking up) and a
/// duplicate tick is harmless.
///
/// Like the sweep function, the Functions host serialises a timer trigger's
/// invocations across instances, so exactly one runs per tick — provided this
/// stays the ONE deployed Function App for this trigger.
/// </summary>
public sealed class BookingReminderFunction(
    IConfiguration configuration,
    IPawfrontSecretProvider secretProvider,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<BookingReminderFunction>();

    [Function("BookingReminderFunction")]
    public async Task Run(
        [TimerTrigger("0 */1 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            await RemindAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // A transient SQL failure must not stop future ticks. Missing one is
            // cheap here: the predicates are windows, not instants, so the next
            // tick re-picks up anything still due.
            _logger.LogError(
                exception, "Booking reminder sweep failed; will retry at the next scheduled tick.");
        }
    }

    private async Task RemindAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        var result = await BookingReminderSweep.RunAsync(connection, cancellationToken);

        // Quiet when there is nothing to say — at 1,440 ticks a day an
        // unconditional log line would bury everything else in the trace.
        if (!result.DidWork)
        {
            return;
        }

        _logger.LogInformation(
            "Booking reminders enqueued: {DayBefore} day-before, {StartingSoon} starting-soon, " +
            "{NotStarted} not-started nudge(s), {StartOtp} start-code nudge(s), {Pickup} pick-up nudge(s). " +
            "Counts are before outbox de-duplication.",
            result.DayBeforeReminders, result.StartingSoonReminders, result.NotStartedNudges,
            result.StartOtpNudges, result.PickupNudges);
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        var configured = configuration.GetConnectionString("SqlServer");
        return string.IsNullOrWhiteSpace(configured)
            ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken)
            : configured;
    }
}

using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Functions.Sweeps;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Clears out chat connections whose heartbeat has stopped, every minute.
///
/// It runs at the reminder function's cadence rather than the 5-minute settlement
/// sweep's, because the cost of being late is asymmetric: until a dead row is
/// gone, its owner looks present and gets NO push for any message. Five minutes
/// of silent notifications is a worse failure than a slightly chattier timer that
/// changes nothing.
///
/// Separate from <see cref="BookingSweepFunction"/> and
/// <see cref="BookingReminderFunction"/> because it touches a different feature
/// entirely — a chat outage should not be able to stall booking settlement, and
/// vice versa.
///
/// As with the other timers, the Functions host serialises a trigger's
/// invocations across instances, so exactly one runs per tick — provided this
/// stays the ONE deployed Function App for this trigger.
/// </summary>
public sealed class ChatPresenceSweepFunction(
    IConfiguration configuration,
    IPawfrontSecretProvider secretProvider,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ChatPresenceSweepFunction>();

    [Function("ChatPresenceSweepFunction")]
    public async Task Run(
        [TimerTrigger("0 */1 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // A transient SQL failure must not stop future ticks. Missing one is
            // cheap: the predicate is an age, not an instant, so the next tick
            // removes whatever is still stale.
            _logger.LogError(
                exception, "Chat presence sweep failed; will retry at the next scheduled tick.");
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        var result = await ChatPresenceSweep.RunAsync(connection, cancellationToken);

        // Quiet when there was nothing to remove — at 1,440 ticks a day an
        // unconditional line would bury everything else in the trace.
        if (!result.DidWork)
        {
            return;
        }

        _logger.LogInformation(
            "Chat presence sweep removed {PurgedCount} stale connection(s).", result.PurgedCount);
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        var configured = configuration.GetConnectionString("SqlServer");
        return string.IsNullOrWhiteSpace(configured)
            ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken)
            : configured;
    }
}

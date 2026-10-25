using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;

namespace Pawfront.Functions.Functions;

/// <summary>Settles attendance and stages Events V3 notifications every 15 seconds.</summary>
public sealed class EventNotificationFunction(
    IConfiguration configuration,
    IPawfrontSecretProvider secretProvider,
    ILogger<EventNotificationFunction> logger)
{
    [Function("EventNotificationFunction")]
    public async Task Run([TimerTrigger("*/15 * * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        try
        {
            var configured = configuration.GetConnectionString("SqlServer");
            var connectionString = string.IsNullOrWhiteSpace(configured)
                ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken) : configured;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var procedure in new[] { "Event.SettleEventAttendance", "Event.SendEventNotifications" })
            {
                await using var command = new SqlCommand(procedure, connection)
                {
                    CommandType = System.Data.CommandType.StoredProcedure,
                    CommandTimeout = 120
                };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Event notification sweep failed; will retry at the next tick.");
        }
    }
}

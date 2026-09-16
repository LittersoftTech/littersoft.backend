using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.Notifications;

/// <summary>
/// Closes out an instantly-sent notification through
/// <c>Notification.CompleteNotificationDelivery</c> — the same procedure and the
/// same table type the Pawfront.Functions dispatcher uses, so the retry backoff,
/// the attempt ceiling and the inbox copy are written by one implementation
/// rather than two that could drift.
/// </summary>
internal sealed class SqlInstantNotificationSender(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    ILogger<SqlInstantNotificationSender> logger) : IInstantNotificationSender
{
    /// <summary>
    /// Mirrors <c>NotificationOptions.Dispatch.MaxAttempts</c>' default. Passed
    /// through so a failure recorded here is retried on the same schedule as one
    /// the dispatcher recorded.
    /// </summary>
    private const int MaxAttempts = 5;

    public async Task CompleteAsync(
        NotificationDeliveryResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand("Notification.CompleteNotificationDelivery", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            var table = new DataTable();
            table.Columns.Add("NotificationId", typeof(Guid));
            table.Columns.Add("Status", typeof(string));
            table.Columns.Add("Title", typeof(string));
            table.Columns.Add("Body", typeof(string));
            table.Columns.Add("Route", typeof(string));
            table.Columns.Add("DeliveredCount", typeof(int));
            table.Columns.Add("LastError", typeof(string));

            table.Rows.Add(
                result.NotificationId,
                result.Status,
                (object?)result.Title ?? DBNull.Value,
                (object?)result.Body ?? DBNull.Value,
                (object?)result.Route ?? DBNull.Value,
                result.DeliveredCount,
                (object?)Truncate(result.LastError, 2000) ?? DBNull.Value);

            var parameter = command.Parameters.AddWithValue("@Results", table);
            parameter.SqlDbType = SqlDbType.Structured;
            parameter.TypeName = "Notification.NotificationDeliveryResultList";

            command.Parameters.AddWithValue("@MaxAttempts", MaxAttempts);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Swallowed on purpose. The message is stored and the push has very
            // likely gone out; failing here would report a problem about a
            // problem. Leaving the row un-completed is also the SAFE outcome — its
            // lease lapses and the ordinary dispatcher takes it over.
            logger.LogError(
                exception,
                "Failed to record the outcome of notification {NotificationId}; " +
                "its lease will lapse and NotificationDispatchFunction will retry it.",
                result.NotificationId);
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private async Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken)
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

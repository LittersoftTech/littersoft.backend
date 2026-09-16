using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Functions.Notifications;

/// <summary>
/// The dispatcher's side of the outbox — claim, complete, prune dead tokens.
///
/// Lives in this project rather than Pawfront.Infrastructure.Sql because only the
/// dispatcher ever reads the outbox (the API hosts only ever write to it, through
/// <c>SqlNotificationPublisher</c>). Talking to SQL directly with
/// <c>Microsoft.Data.SqlClient</c> also matches how the three booking sweeps in
/// <c>Sweeps/</c> already work, and keeps this host free of the API-side
/// registration graph.
///
/// Unlike the publisher, this one DOES let exceptions surface: the dispatcher's
/// whole job is these calls, so a failure must be visible and retried on the next
/// tick rather than silently swallowed.
/// </summary>
public sealed class SqlNotificationOutboxStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    ILogger<SqlNotificationOutboxStore> logger) : INotificationOutboxStore
{
    public async Task<NotificationClaimBatch> ClaimAsync(
        int batchSize,
        int maxAttempts,
        int leaseMinutes,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Notification.ClaimPendingNotifications", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@BatchSize", batchSize);
        command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
        command.Parameters.AddWithValue("@LeaseMinutes", leaseMinutes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1 — the claimed work.
        var notifications = new List<ClaimedNotification>();
        while (await reader.ReadAsync(cancellationToken))
        {
            notifications.Add(new ClaimedNotification(
                reader.GetGuid(0),
                NotificationAudiences.FromSqlValue(reader.GetString(1)),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero)));
        }

        if (notifications.Count == 0)
        {
            return NotificationClaimBatch.Empty;
        }

        // Result set 2 — the devices to push to, pre-joined.
        var tokens = new List<RecipientDeviceToken>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tokens.Add(new RecipientDeviceToken(
                    NotificationAudiences.FromSqlValue(reader.GetString(0)),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new NotificationClaimBatch(notifications, tokens);
    }

    public async Task CompleteAsync(
        IReadOnlyList<NotificationDeliveryResult> results,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

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

        foreach (var result in results)
        {
            table.Rows.Add(
                result.NotificationId,
                result.Status,
                (object?)result.Title ?? DBNull.Value,
                (object?)result.Body ?? DBNull.Value,
                (object?)result.Route ?? DBNull.Value,
                result.DeliveredCount,
                (object?)Truncate(result.LastError, 2000) ?? DBNull.Value);
        }

        var parameter = command.Parameters.AddWithValue("@Results", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "Notification.NotificationDeliveryResultList";

        command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeactivateTokensAsync(
        IReadOnlyList<DeadDeviceToken> tokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Notification.DeactivateDeviceTokens", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        var table = new DataTable();
        table.Columns.Add("Audience", typeof(string));
        table.Columns.Add("FcmToken", typeof(string));

        // Distinct: the same dead token commonly appears across several
        // notifications in one batch.
        foreach (var token in tokens.DistinctBy(t => (t.Audience, t.FcmToken)))
        {
            table.Rows.Add(token.Audience.ToSqlValue(), token.FcmToken);
        }

        var parameter = command.Parameters.AddWithValue("@Tokens", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "Notification.DeviceTokenList";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var providerCount = reader.GetInt32(0);
            var parentCount = reader.GetInt32(1);

            if (providerCount > 0 || parentCount > 0)
            {
                logger.LogInformation(
                    "Deactivated {ProviderTokens} provider and {ParentTokens} pet-parent FCM token(s) " +
                    "that Firebase reported as permanently invalid.",
                    providerCount, parentCount);
            }
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
                "SQL Server connection string is not configured and no Key Vault secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}

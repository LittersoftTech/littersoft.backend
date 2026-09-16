using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.Notifications;

/// <summary>
/// Writes notifications to <c>Notification.NotificationOutbox</c> via
/// <c>Notification.EnqueueNotification</c>.
///
/// <b>Never throws for an enqueue failure.</b> A notification is a side effect of
/// a booking transition, never its purpose — failing the caller because a push
/// could not be queued would turn a cosmetic problem into a lost booking. Errors
/// are logged and <see cref="Guid.Empty"/> is returned.
/// </summary>
internal sealed class SqlNotificationPublisher(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    ILogger<SqlNotificationPublisher> logger) : INotificationPublisher
{
    public async Task<Guid> PublishAsync(
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
            await connection.OpenAsync(cancellationToken);

            return await EnqueueAsync(connection, request, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to enqueue {NotificationType} notification for {Audience} {RecipientId}.",
                request.NotificationType, request.Audience, request.RecipientId);

            return Guid.Empty;
        }
    }

    public async Task PublishManyAsync(
        IReadOnlyList<NotificationRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        try
        {
            // One connection for the batch — the common case is the two sides of
            // a single event (the parent's copy and the provider's).
            await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
            await connection.OpenAsync(cancellationToken);

            foreach (var request in requests)
            {
                try
                {
                    await EnqueueAsync(connection, request, cancellationToken);
                }
                catch (Exception exception)
                {
                    // Per-item so one bad recipient doesn't cost the others their
                    // notification.
                    logger.LogError(
                        exception,
                        "Failed to enqueue {NotificationType} notification for {Audience} {RecipientId}.",
                        request.NotificationType, request.Audience, request.RecipientId);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to enqueue a batch of {Count} notification(s).", requests.Count);
        }
    }

    private static async Task<Guid> EnqueueAsync(
        SqlConnection connection,
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Notification.EnqueueNotification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@Audience", request.Audience.ToSqlValue());
        command.Parameters.AddWithValue("@RecipientId", request.RecipientId);
        command.Parameters.AddWithValue("@NotificationType", request.NotificationType);
        command.Parameters.AddWithValue("@EntityType", (object?)request.EntityType ?? DBNull.Value);
        command.Parameters.AddWithValue("@EntityId", (object?)request.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@DataJson",
            (object?)NotificationPayloadBuilder.SerializeData(request.Data) ?? DBNull.Value);
        command.Parameters.AddWithValue("@ImageUrl", (object?)request.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@DedupeKey", (object?)request.DedupeKey ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0)
            ? reader.GetGuid(0)
            : Guid.Empty;
    }

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

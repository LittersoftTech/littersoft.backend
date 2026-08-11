using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Chat;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Chat;

/// <summary>
/// Live connections, through the <c>Chat</c> schema's presence procedures.
///
/// Only the WRITE side is here. Whether a message earns a push is read inside
/// <c>Chat.CommitMessageAppend</c>, in the same transaction as the message itself, so
/// presence cannot change between the decision and the write.
/// </summary>
internal sealed class SqlChatPresenceStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IChatPresenceStore
{
    public async Task SaveConnectionAsync(
        string connectionId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[SaveChatConnection]");
        command.Parameters.AddWithValue("@ConnectionId", connectionId);
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[DeleteChatConnection]");
        command.Parameters.AddWithValue("@ConnectionId", connectionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetActiveConversationAsync(
        string connectionId,
        ChatParticipant participant,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[SetActiveConversation]");
        command.Parameters.AddWithValue("@ConnectionId", connectionId);
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);
        command.Parameters.AddWithValue("@ConversationId", (object?)conversationId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeStaleAsync(int staleMinutes, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[PurgeStaleConnections]");
        command.Parameters.AddWithValue("@StaleMinutes", staleMinutes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : 0;
    }

    private static SqlCommand StoredProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

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

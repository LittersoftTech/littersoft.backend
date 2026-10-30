using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.DeviceTokens;

internal sealed class SqlProviderPushTokenReader(
    string? configuredConnectionString, IPawfrontSecretProvider? secretProvider) : IProviderPushTokenReader
{
    public async Task<IReadOnlyList<string>> GetCurrentTokensAsync(string firebaseUserId, CancellationToken cancellationToken)
    {
        var cs = !string.IsNullOrWhiteSpace(configuredConnectionString) ? configuredConnectionString
            : secretProvider is not null ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken)
            : throw new InvalidOperationException("SQL Server is not configured.");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT t.FcmToken
            FROM Provider.ProviderDeviceTokens t
            JOIN Provider.ProviderAuthIdentities a
                ON a.ProviderAuthIdentityId = t.ProviderAuthIdentityId AND a.ProviderId = t.ProviderId
            JOIN Provider.Providers p
                ON p.ProviderId = t.ProviderId AND p.ProviderAuthIdentityId = t.ProviderAuthIdentityId
            WHERE a.FirebaseUserId = @FirebaseUserId AND t.IsActive = 1 AND p.IsDeleted = 0;
            """, connection);
        command.Parameters.Add("@FirebaseUserId", SqlDbType.NVarChar, 128).Value = firebaseUserId;
        var tokens = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tokens.Add(reader.GetString(0));
        return tokens;
    }
}

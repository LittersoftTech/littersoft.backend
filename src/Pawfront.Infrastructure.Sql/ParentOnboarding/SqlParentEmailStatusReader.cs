using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

internal sealed class SqlParentEmailStatusReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IParentEmailStatusReader
{
    public async Task<bool> IsRegisteredAsync(string email, CancellationToken cancellationToken)
    {
        var connectionString = !string.IsNullOrWhiteSpace(configuredConnectionString)
            ? configuredConnectionString
            : secretProvider is not null
                ? await secretProvider.GetSqlConnectionStringAsync(cancellationToken)
                : throw new InvalidOperationException("SQL Server is not configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1
                FROM [Parent].[ParentAuthIdentities] AS a
                INNER JOIN [Parent].[PetParents] AS p
                    ON p.[ParentAuthIdentityId] = a.[ParentAuthIdentityId]
                WHERE p.[IsDeleted] = 0
                  AND LTRIM(RTRIM(a.[Email])) COLLATE Latin1_General_100_CI_AS = @Email
            ) THEN 1 ELSE 0 END AS BIT);
            """, connection);
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 320).Value = email.Trim();
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

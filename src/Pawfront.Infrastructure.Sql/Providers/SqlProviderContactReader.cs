using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// Reads a provider's account contact — the sign-in e-mail from
/// <c>Provider.ProviderAuthIdentities</c> and the verified mobile from
/// <c>Provider.Providers</c> — in one indexed point read, mirroring
/// <see cref="SqlProviderNameReader"/>. Used to fill the parent-facing provider
/// detail for freelancers, whose offering carries no business e-mail/telephone.
/// </summary>
internal sealed class SqlProviderContactReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderContactReader
{
    public async Task<ProviderContactDetails?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // LEFT JOIN: a profile row always has an auth identity, but the join keeps
        // the mobile readable even if that ever stops holding.
        await using var command = new SqlCommand(
            "SELECT ai.[Email], p.[MobileCountryCode], p.[MobileNumber] " +
            "FROM [Provider].[Providers] p " +
            "LEFT JOIN [Provider].[ProviderAuthIdentities] ai " +
            "    ON ai.[ProviderAuthIdentityId] = p.[ProviderAuthIdentityId] " +
            "WHERE p.[ProviderId] = @ProviderId;",
            connection);
        command.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProviderContactDetails(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

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

using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// One batched membership query behind the discovery active filter. Batched
/// rather than per-provider because discovery hands over a whole page of
/// candidates at once — the same posture as
/// <c>SqlProviderBookingStatsReader</c>.
/// </summary>
internal sealed class SqlProviderActiveStatusReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderActiveStatusReader
{
    public async Task<IReadOnlySet<Guid>> GetActiveProviderIdsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // IsActive is the provider's own master switch (POST
        // /providers/{id}/active-status). IsDeleted is checked too even though
        // the delete forces IsActive = 0 as well: it is the permanent flag, and
        // relying on only one of the two would make discovery depend on the
        // delete sproc never being changed.
        await using var command = new SqlCommand(
            "SELECT p.[ProviderId] " +
            "FROM [Provider].[Providers] p " +
            "INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "    ON p.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]) " +
            "WHERE p.[IsActive] = 1 AND p.[IsDeleted] = 0;",
            connection);
        command.Parameters.AddWithValue(
            "@ProviderIds", string.Join(',', providerIds.Select(id => id.ToString("D"))));

        var active = new HashSet<Guid>(providerIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            active.Add(reader.GetGuid(0));
        }
        return active;
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

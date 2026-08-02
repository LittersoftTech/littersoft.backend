using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// Reads provider personal names (FirstName + LastName) from
/// <c>Provider.Providers</c> in a single batched round-trip, mirroring
/// <see cref="Bookings.SqlProviderBookingStatsReader"/>. Used to fill the
/// freelancer name on parent-facing cards where the Cosmos offering has no
/// business name.
/// </summary>
internal sealed class SqlProviderNameReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderNameReader
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetProviderDisplayNamesAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT p.[ProviderId], p.[FirstName], p.[LastName] " +
            "FROM [Provider].[Providers] p " +
            "INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "    ON p.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]);",
            connection);
        command.Parameters.AddWithValue(
            "@ProviderIds", string.Join(',', providerIds.Select(id => id.ToString("D"))));

        var names = new Dictionary<Guid, string>(providerIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var providerId = reader.GetGuid(0);
            var firstName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var lastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var displayName = $"{firstName} {lastName}".Trim();
            if (displayName.Length > 0)
            {
                names[providerId] = displayName;
            }
        }
        return names;
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

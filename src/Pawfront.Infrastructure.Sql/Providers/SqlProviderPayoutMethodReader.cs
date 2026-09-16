using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

/// <summary>
/// One batched read behind the parent-facing "Payments" search filter. Inline
/// SQL rather than a stored procedure — it is a single indexed lookup over a
/// junction table with no rules attached, exactly like
/// <see cref="SqlProviderActiveStatusReader"/>, so it needs no
/// <c>DeployAll.sql</c> change.
/// </summary>
internal sealed class SqlProviderPayoutMethodReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderPayoutMethodReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> GetPayoutMethodsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyCollection<string>>();
        }

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT pm.[ProviderId], pm.[PayoutMethod] " +
            "FROM [Provider].[ProviderPayoutMethods] pm " +
            "INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "    ON pm.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]);",
            connection);
        command.Parameters.AddWithValue(
            "@ProviderIds", string.Join(',', providerIds.Select(id => id.ToString("D"))));

        var accumulator = new Dictionary<Guid, List<string>>(providerIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var providerId = reader.GetGuid(0);
            if (!accumulator.TryGetValue(providerId, out var methods))
            {
                methods = [];
                accumulator[providerId] = methods;
            }
            methods.Add(reader.GetString(1));
        }

        return accumulator.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyCollection<string>)kvp.Value);
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

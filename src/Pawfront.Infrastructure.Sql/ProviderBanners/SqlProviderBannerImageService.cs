using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderBanners;

namespace Pawfront.Infrastructure.Sql.ProviderBanners;

internal sealed class SqlProviderBannerImageService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderBannerImageService
{
    public async Task<ProviderBannerImageResult> SaveAsync(
        Guid providerId,
        string bannerImageUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(bannerImageUrl, nameof(bannerImageUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.UpdateProviderBannerImage");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@BannerImageUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider banner row was not returned after save.");
            }

            return new ProviderBannerImageResult(
                reader.GetGuid(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51112)
        {
            throw new ProviderBannerImageProviderNotFoundException(providerId);
        }
    }

    public async Task<string?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT [BannerImageUrl] FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId;",
            connection);
        command.Parameters.AddWithValue("@ProviderId", providerId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetByProviderIdsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // Batch point-read the banner column for the requested providers. Mirrors
        // the STRING_SPLIT batch pattern used by SqlProviderServiceBannerService.
        await using var command = new SqlCommand(
            "SELECT p.[ProviderId], p.[BannerImageUrl] " +
            "FROM [Provider].[Providers] p " +
            "INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "    ON p.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]) " +
            "WHERE p.[BannerImageUrl] IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue(
            "@ProviderIds", string.Join(',', providerIds.Select(id => id.ToString("D"))));

        var banners = new Dictionary<Guid, string>(providerIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            banners[reader.GetGuid(0)] = reader.GetString(1);
        }
        return banners;
    }

    private static SqlCommand CreateStoredProcedureCommand(SqlConnection connection, string storedProcedureName) =>
        new(storedProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure
        };

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

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", name);
        }

        return value.Trim();
    }
}

using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderServiceBanners;

namespace Pawfront.Infrastructure.Sql.ProviderServiceBanners;

internal sealed class SqlProviderServiceBannerService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderServiceBannerService
{
    public async Task<ProviderServiceBannerResult> SaveAsync(
        Guid providerId,
        Guid serviceId,
        string bannerImageUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(bannerImageUrl, nameof(bannerImageUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.SaveProviderServiceBanner");
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@BannerImageUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Service banner row was not returned after upsert.");
            }
            return ReadBanner(reader);
        }
        catch (SqlException exception) when (exception.Number == 51081)
        {
            throw new ProviderServiceBannerServiceNotFoundException(serviceId, providerId);
        }
    }

    public async Task<ProviderServiceBannerResult?> GetAsync(
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.GetProviderServiceBanner");
        command.Parameters.AddWithValue("@ServiceId", serviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadBanner(reader);
    }

    private static ProviderServiceBannerResult ReadBanner(SqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));

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

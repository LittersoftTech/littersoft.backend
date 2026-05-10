using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Services.ProviderServiceLocations;

namespace Pawfront.Infrastructure.Sql.ProviderServices;

internal sealed class SqlProviderServiceLocationRegistry(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderServiceLocationRegistry
{
    public async Task<ProviderServiceLocation> SaveAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.SaveProviderServiceRegistration", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
        command.Parameters.AddWithValue("@Latitude", latitude);
        command.Parameters.AddWithValue("@Longitude", longitude);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider service registration was not returned after save.");
            }

            return new ProviderServiceLocation(
                ProviderServiceRegistrationId: reader.GetGuid(0),
                ProviderId: reader.GetGuid(1),
                ServiceCategory: reader.GetString(2),
                SubCategory: reader.GetString(3),
                Latitude: reader.GetDecimal(4),
                Longitude: reader.GetDecimal(5),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
                UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51010)
        {
            throw new ProviderServiceLocationProviderNotFoundException(providerId);
        }
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

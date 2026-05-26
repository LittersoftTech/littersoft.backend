using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderServices;

namespace Pawfront.Infrastructure.Sql.ProviderServices;

internal sealed class SqlProviderServiceCatalog(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderServiceCatalog
{
    public async Task<ProviderService> UpsertAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        string serviceType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.UpsertProviderService", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
        command.Parameters.AddWithValue("@ServiceType", serviceType);

        var serviceIdParam = new SqlParameter("@ServiceId", SqlDbType.UniqueIdentifier)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        };
        command.Parameters.Add(serviceIdParam);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider service row was not returned after upsert.");
            }
            return ReadProviderService(reader);
        }
        catch (SqlException exception) when (exception.Number == 51080)
        {
            throw new ProviderServiceCatalogProviderNotFoundException(providerId);
        }
    }

    public async Task DeactivateAsync(Guid providerId, string serviceType, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.DeactivateProviderService", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceType", serviceType);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderService>> ListByProviderAsync(
        Guid providerId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.ListProviderServices", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@IncludeInactive", includeInactive ? 1 : 0);

        var rows = new List<ProviderService>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadProviderService(reader));
        }
        return rows;
    }

    public async Task<ProviderService?> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.GetProviderService", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ServiceId", serviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadProviderService(reader);
    }

    private static ProviderService ReadProviderService(SqlDataReader reader) => new(
        ServiceId: reader.GetGuid(0),
        ProviderId: reader.GetGuid(1),
        ServiceCategory: reader.GetString(2),
        SubCategory: reader.GetString(3),
        ServiceType: reader.GetString(4),
        IsActive: reader.GetBoolean(5),
        CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
        UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));

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

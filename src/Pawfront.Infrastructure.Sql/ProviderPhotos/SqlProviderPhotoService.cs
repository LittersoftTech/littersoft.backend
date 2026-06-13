using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderPhotos;
using Pawfront.Contracts.ProviderPhotos;

namespace Pawfront.Infrastructure.Sql.ProviderPhotos;

internal sealed class SqlProviderPhotoService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderPhotoService
{
    public async Task<ProviderPhotoResponse> AddAsync(
        Guid providerId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(photoUrl, nameof(photoUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.AddProviderPhoto");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PhotoUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider photo row was not returned after insert.");
            }

            return ReadPhoto(reader);
        }
        catch (SqlException exception) when (exception.Number == 51110)
        {
            throw new ProviderPhotoProviderNotFoundException(providerId);
        }
    }

    public async Task<IReadOnlyList<ProviderPhotoResponse>> ListAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.ListProviderPhotos");
        command.Parameters.AddWithValue("@ProviderId", providerId);

        var photos = new List<ProviderPhotoResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            photos.Add(ReadPhoto(reader));
        }

        return photos;
    }

    public async Task<DeleteProviderPhotoResponse> DeleteAsync(
        Guid providerId,
        Guid providerPhotoId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Provider.DeleteProviderPhoto");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ProviderPhotoId", providerPhotoId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Deleted provider photo row was not returned.");
            }

            return new DeleteProviderPhotoResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51111)
        {
            throw new ProviderPhotoNotFoundException(providerPhotoId);
        }
    }

    private static ProviderPhotoResponse ReadPhoto(SqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));

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

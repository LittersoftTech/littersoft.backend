using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPhotos;
using Pawfront.Contracts.ParentPhotos;

namespace Pawfront.Infrastructure.Sql.ParentPhotos;

internal sealed class SqlPetParentPhotoService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IPetParentPhotoService
{
    public async Task<PetParentPhotoResponse> AddAsync(
        Guid petParentId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(photoUrl, nameof(photoUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Parent.AddPetParentPhoto");
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PhotoUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent photo row was not returned after insert.");
            }

            return ReadPhoto(reader);
        }
        catch (SqlException exception) when (exception.Number == 51212)
        {
            throw new PetParentNotFoundException(petParentId);
        }
    }

    public async Task<IReadOnlyList<PetParentPhotoResponse>> ListAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Parent.ListPetParentPhotos");
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        var photos = new List<PetParentPhotoResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            photos.Add(ReadPhoto(reader));
        }

        return photos;
    }

    public async Task<DeletePetParentPhotoResponse> DeleteAsync(
        Guid petParentId,
        Guid petParentPhotoId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(connection, "Parent.DeletePetParentPhoto");
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PetParentPhotoId", petParentPhotoId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Deleted pet parent photo row was not returned.");
            }

            return new DeletePetParentPhotoResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51213)
        {
            throw new PetParentPhotoNotFoundException(petParentPhotoId);
        }
    }

    private static PetParentPhotoResponse ReadPhoto(SqlDataReader reader) =>
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

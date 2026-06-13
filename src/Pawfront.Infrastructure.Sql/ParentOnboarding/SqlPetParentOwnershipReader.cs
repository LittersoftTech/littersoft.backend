using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

internal sealed class SqlPetParentOwnershipReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IPetParentOwnershipReader
{
    public async Task<Guid?> GetPetParentIdByFirebaseUserIdAsync(
        string firebaseUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // Lookup hits UQ_ParentAuthIdentities_FirebaseUserId. PetParentId is
        // NULL when the parent has the Firebase auth row but hasn't completed
        // their profile yet (POST /parent-onboarding/profile hasn't run).
        await using var command = new SqlCommand(
            "SELECT [PetParentId] " +
            "FROM [Parent].[ParentAuthIdentities] " +
            "WHERE [FirebaseUserId] = @FirebaseUserId;",
            connection);
        command.Parameters.AddWithValue("@FirebaseUserId", firebaseUserId);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null || raw is DBNull)
        {
            return null;
        }

        return (Guid)raw;
    }

    public async Task<Guid?> GetOwningPetParentIdByPetIdAsync(
        Guid petId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // Lookup hits PK_Pets. Returns null when the pet row doesn't exist —
        // the ownership filter surfaces that as 404 rather than 403.
        await using var command = new SqlCommand(
            "SELECT [PetParentId] " +
            "FROM [Parent].[Pets] " +
            "WHERE [PetId] = @PetId;",
            connection);
        command.Parameters.AddWithValue("@PetId", petId);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null || raw is DBNull)
        {
            return null;
        }

        return (Guid)raw;
    }

    public async Task<PetOwnershipLookup?> GetPetLookupAsync(
        Guid petId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // Lookup hits PK_Pets. Owner + pet type in one point read so the
        // discovery endpoint's petId filter pays a single round-trip.
        await using var command = new SqlCommand(
            "SELECT [PetParentId], [PetType] " +
            "FROM [Parent].[Pets] " +
            "WHERE [PetId] = @PetId;",
            connection);
        command.Parameters.AddWithValue("@PetId", petId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PetOwnershipLookup(
            reader.GetGuid(0),
            reader.GetString(1));
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

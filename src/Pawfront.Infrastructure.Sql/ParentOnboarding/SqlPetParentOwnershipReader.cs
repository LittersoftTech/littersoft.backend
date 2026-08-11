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
        //
        // A soft-deleted pet reads as "doesn't exist" here, which is what makes
        // this the single gate for the whole /pets/{petId}/* group: every read
        // and every mutation behind it 404s without each sproc needing its own
        // IsDeleted guard. Booking READS deliberately still join the row — that
        // history is why it is kept.
        await using var command = new SqlCommand(
            "SELECT [PetParentId] " +
            "FROM [Parent].[Pets] " +
            "WHERE [PetId] = @PetId AND [IsDeleted] = 0;",
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

        // Lookup hits PK_Pets, and the owner join hits PK_PetParents. Owner +
        // pet type + pet name + owner name in one point read so the discovery
        // endpoint's petId filter pays a single round-trip, and the booking
        // creates get both names for the provider's notification without a
        // second query.
        // LEFT JOIN deliberately: this reader's primary job is the ownership
        // answer, and it must not start returning null because a name couldn't
        // be resolved.
        // Soft-deleted pets read as missing here too, so the discovery petId
        // filter and both booking creates reject them before SQL is reached.
        await using var command = new SqlCommand(
            "SELECT p.[PetParentId], p.[PetType], p.[PetName], " +
            "NULLIF(LTRIM(RTRIM(CONCAT(pp.[FirstName], N' ', pp.[LastName]))), N'') " +
            "FROM [Parent].[Pets] AS p " +
            "LEFT JOIN [Parent].[PetParents] AS pp ON pp.[PetParentId] = p.[PetParentId] " +
            "WHERE p.[PetId] = @PetId AND p.[IsDeleted] = 0;",
            connection);
        command.Parameters.AddWithValue("@PetId", petId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PetOwnershipLookup(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
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

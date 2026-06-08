using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

internal sealed class SqlPetParentOnboardingStatusReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IPetParentOnboardingStatusReader
{
    public async Task<PetParentOnboardingStatusSnapshot?> ReadAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Parent.GetPetParentOnboardingStatus", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: parent profile + auth-identity verification flags.
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var profilePhotoUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
        var isMobileVerified = !reader.IsDBNull(2);
        var isEmailVerified = reader.GetBoolean(3);

        // Result set 2: pets summary.
        var pets = new List<PetMedicalInfoCompletion>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pets.Add(new PetMedicalInfoCompletion(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetBoolean(2)));
            }
        }

        // Result set 3: identity. Zero rows = no upload (Remaining); one row
        // carries the IdentityType.
        string? identityType = null;
        if (await reader.NextResultAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                identityType = reader.GetString(0);
            }
        }

        return new PetParentOnboardingStatusSnapshot(
            petParentId,
            profilePhotoUrl,
            isEmailVerified,
            isMobileVerified,
            pets,
            identityType);
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

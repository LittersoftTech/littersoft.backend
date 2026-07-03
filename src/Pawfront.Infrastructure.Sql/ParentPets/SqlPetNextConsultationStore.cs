using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentPets;

namespace Pawfront.Infrastructure.Sql.ParentPets;

internal sealed class SqlPetNextConsultationStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IPetNextConsultationStore
{
    public async Task UpsertAsync(
        Guid petId,
        string consultationType,
        DateOnly nextConsultationDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Parent.UpsertPetNextConsultation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetId", petId);
        command.Parameters.AddWithValue("@ConsultationType", consultationType);
        command.Parameters.AddWithValue("@NextConsultationDate", nextConsultationDate.ToDateTime(TimeOnly.MinValue));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51221)
        {
            throw new PetNotFoundException(petId);
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
                "SQL Server connection string is not configured and no Key Vault secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}

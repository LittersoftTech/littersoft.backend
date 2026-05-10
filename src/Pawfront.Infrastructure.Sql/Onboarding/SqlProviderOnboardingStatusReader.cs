using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Onboarding;

namespace Pawfront.Infrastructure.Sql.Onboarding;

internal sealed class SqlProviderOnboardingStatusReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderOnboardingStatusReader
{
    public async Task<ProviderOnboardingStatusSnapshot?> ReadAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.GetProviderOnboardingStatus", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: provider profile + verification.
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var isMobileVerified = !reader.IsDBNull(1);
        var isEmailVerified = reader.GetBoolean(2);

        // Result set 2: registered categories.
        var registeredCategories = new List<RegisteredCategory>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                registeredCategories.Add(new RegisteredCategory(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }

        // Result set 3: payout methods.
        var payoutMethods = new List<string>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                payoutMethods.Add(reader.GetString(0));
            }
        }

        // Result set 4: cancellation policy (zero or one row).
        var hasCancellationRow = false;
        int? minimumHoursBeforeCancellation = null;
        if (await reader.NextResultAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                hasCancellationRow = true;
                minimumHoursBeforeCancellation = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            }
        }

        return new ProviderOnboardingStatusSnapshot(
            providerId,
            isMobileVerified,
            isEmailVerified,
            registeredCategories,
            payoutMethods,
            hasCancellationRow,
            minimumHoursBeforeCancellation);
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

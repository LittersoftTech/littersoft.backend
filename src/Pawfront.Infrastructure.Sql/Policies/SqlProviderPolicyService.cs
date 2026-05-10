using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Policies;

namespace Pawfront.Infrastructure.Sql.Policies;

internal sealed class SqlProviderPolicyService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderPolicyService
{
    public async Task<ProviderPolicyResult> SavePayoutMethodsAsync(
        Guid providerId,
        IReadOnlyCollection<string> payoutMethods,
        CancellationToken cancellationToken)
    {
        var (acceptsCash, acceptsDigital) = NormalizePayoutMethods(payoutMethods);

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.SaveProviderPayoutMethods", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@AcceptsCash", acceptsCash);
        command.Parameters.AddWithValue("@AcceptsDigital", acceptsDigital);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var savedMethods = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                savedMethods.Add(reader.GetString(0));
            }

            return await GetAsync(providerId, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51020)
        {
            throw new ProviderPolicyProviderNotFoundException(providerId);
        }
    }

    public async Task<ProviderPolicyResult> SaveCancellationPolicyAsync(
        Guid providerId,
        int? minimumHoursBeforeCancellation,
        CancellationToken cancellationToken)
    {
        ValidateCancellationHours(minimumHoursBeforeCancellation);

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.SaveProviderCancellationPolicy", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue(
            "@MinimumHoursBeforeCancellation",
            minimumHoursBeforeCancellation.HasValue ? (object)minimumHoursBeforeCancellation.Value : DBNull.Value);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return await GetAsync(providerId, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51021)
        {
            throw new ProviderPolicyProviderNotFoundException(providerId);
        }
    }

    public async Task<ProviderPolicyResult> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.GetProviderPolicy", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var methods = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            methods.Add(reader.GetString(0));
        }

        int? cancellationHours = null;
        DateTimeOffset? updatedAtUtc = null;

        if (await reader.NextResultAsync(cancellationToken)
            && await reader.ReadAsync(cancellationToken))
        {
            cancellationHours = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            updatedAtUtc = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero);
        }

        return new ProviderPolicyResult(providerId, methods, cancellationHours, updatedAtUtc);
    }

    private static (bool AcceptsCash, bool AcceptsDigital) NormalizePayoutMethods(
        IReadOnlyCollection<string>? payoutMethods)
    {
        if (payoutMethods is null)
        {
            throw new ArgumentException("Payout methods are required.", nameof(payoutMethods));
        }

        var acceptsCash = false;
        var acceptsDigital = false;

        foreach (var raw in payoutMethods)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!ProviderPayoutMethods.Allowed.Contains(trimmed))
            {
                throw new ArgumentException(
                    $"Payout method '{trimmed}' is not supported.",
                    nameof(payoutMethods));
            }

            if (trimmed == ProviderPayoutMethods.Cash) acceptsCash = true;
            if (trimmed == ProviderPayoutMethods.Digital) acceptsDigital = true;
        }

        if (!acceptsCash && !acceptsDigital)
        {
            throw new ArgumentException(
                "At least one payout method must be selected.",
                nameof(payoutMethods));
        }

        return (acceptsCash, acceptsDigital);
    }

    private static void ValidateCancellationHours(int? hours)
    {
        if (hours.HasValue && !ProviderCancellationPolicyHours.Allowed.Contains(hours.Value))
        {
            throw new ArgumentException(
                $"Cancellation policy hours '{hours.Value}' is not supported. Allowed: 24, 48, 72, 96, or null for no policy.",
                nameof(hours));
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

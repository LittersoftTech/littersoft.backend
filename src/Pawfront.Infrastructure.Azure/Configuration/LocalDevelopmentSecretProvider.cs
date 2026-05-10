using Microsoft.Extensions.Configuration;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Azure.Configuration;

internal sealed class LocalDevelopmentSecretProvider(IConfiguration configuration) : IPawfrontSecretProvider
{
    public Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Required(
            configuration.GetConnectionString("SqlServer"),
            "ConnectionStrings:SqlServer"));
    }

    public Task<string> GetCosmosKeyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Required(configuration["Cosmos:Key"], "Cosmos:Key"));
    }

    public Task<string> GetBlobStorageKeyAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Required(
            configuration["BlobStorage:ConnectionString"],
            "BlobStorage:ConnectionString"));
    }

    public Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken)
    {
        return Task.FromResult(Required(
            configuration[$"LocalSecrets:{secretName}"],
            $"LocalSecrets:{secretName}"));
    }

    private static string Required(string? value, string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Local development secret '{configurationPath}' is not configured. " +
                "Set it in appsettings.Development.json or user secrets, " +
                "or enable Azure Key Vault by setting 'AzureKeyVault:Enabled' to true.");
        }

        return value;
    }
}

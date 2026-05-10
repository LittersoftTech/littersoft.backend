using System.Collections.Concurrent;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Azure.KeyVault;

internal sealed class AzureKeyVaultSecretProvider(
    SecretClient secretClient,
    IOptions<AzureKeyVaultOptions> options) : IPawfrontSecretProvider
{
    private readonly ConcurrentDictionary<string, string> secretCache = new(StringComparer.Ordinal);

    public Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken)
    {
        return GetSecretValueAsync(options.Value.Secrets.SqlConnectionString, cancellationToken);
    }

    public Task<string> GetCosmosKeyAsync(CancellationToken cancellationToken)
    {
        return GetSecretValueAsync(options.Value.Secrets.CosmosKey, cancellationToken);
    }

    public Task<string> GetBlobStorageKeyAsync(CancellationToken cancellationToken)
    {
        return GetSecretValueAsync(options.Value.Secrets.BlobStorageKey, cancellationToken);
    }

    public async Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new InvalidOperationException("Azure Key Vault secret name is required.");
        }

        if (secretCache.TryGetValue(secretName, out var cachedSecret))
        {
            return cachedSecret;
        }

        var response = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
        var secretValue = response.Value.Value;

        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new InvalidOperationException($"Azure Key Vault secret '{secretName}' is empty.");
        }

        secretCache[secretName] = secretValue;

        return secretValue;
    }
}

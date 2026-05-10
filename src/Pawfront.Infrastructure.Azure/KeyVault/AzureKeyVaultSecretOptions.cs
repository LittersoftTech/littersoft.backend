namespace Pawfront.Infrastructure.Azure.KeyVault;

internal sealed class AzureKeyVaultSecretOptions
{
    public string SqlConnectionString { get; init; } = "SQLKey";
    public string CosmosKey { get; init; } = "CosmosKey";
    public string BlobStorageKey { get; init; } = "BlobStorageKey";
}

namespace Pawfront.Infrastructure.Azure.KeyVault;

internal sealed class AzureKeyVaultOptions
{
    public bool Enabled { get; init; } = true;
    public string VaultUri { get; init; } = string.Empty;
    public AzureKeyVaultSecretOptions Secrets { get; init; } = new();
}

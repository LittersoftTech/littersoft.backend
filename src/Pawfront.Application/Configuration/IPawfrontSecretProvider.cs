namespace Pawfront.Application.Configuration;

public interface IPawfrontSecretProvider
{
    Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken);
    Task<string> GetCosmosKeyAsync(CancellationToken cancellationToken);
    Task<string> GetBlobStorageKeyAsync(CancellationToken cancellationToken);
    Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken);
}

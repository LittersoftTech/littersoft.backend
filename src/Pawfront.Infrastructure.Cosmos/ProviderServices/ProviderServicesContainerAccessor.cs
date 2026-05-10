using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Infrastructure.Cosmos.Provisioning;

namespace Pawfront.Infrastructure.Cosmos.ProviderServices;

internal sealed class ProviderServicesContainerAccessor(
    IPawfrontSecretProvider secretProvider,
    IOptions<CosmosOptions> cosmosOptions) : IProviderServicesContainerAccessor, IAsyncDisposable
{
    private readonly CosmosOptions options = cosmosOptions.Value;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private CosmosClient? client;
    private Container? container;

    public async Task<Container> GetContainerAsync(CancellationToken cancellationToken)
    {
        if (container is not null)
        {
            return container;
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (container is not null)
            {
                return container;
            }

            if (string.IsNullOrWhiteSpace(options.Endpoint))
            {
                throw new InvalidOperationException("Cosmos:Endpoint is required.");
            }

            if (string.IsNullOrWhiteSpace(options.DatabaseName))
            {
                throw new InvalidOperationException("Cosmos:DatabaseName is required.");
            }

            if (string.IsNullOrWhiteSpace(options.Containers.ProviderServices))
            {
                throw new InvalidOperationException("Cosmos:Containers:ProviderServices is required.");
            }

            var key = CosmosClientFactory.LooksLikeConnectionString(options.Endpoint)
                ? null
                : await secretProvider.GetCosmosKeyAsync(cancellationToken);

            client = CosmosClientFactory.Create(options.Endpoint, key);
            container = client.GetContainer(options.DatabaseName, options.Containers.ProviderServices);

            return container;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        semaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}

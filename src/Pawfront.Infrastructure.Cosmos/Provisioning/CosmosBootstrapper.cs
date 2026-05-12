using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Infrastructure.Cosmos.Documents;

namespace Pawfront.Infrastructure.Cosmos.Provisioning;

internal sealed class CosmosBootstrapper(
    IPawfrontSecretProvider secretProvider,
    IOptions<CosmosOptions> cosmosOptions,
    ILogger<CosmosBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = cosmosOptions.Value;

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            logger.LogWarning("Cosmos:Endpoint is not configured; skipping container bootstrap.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            logger.LogWarning("Cosmos:DatabaseName is not configured; skipping container bootstrap.");
            return;
        }

        var key = CosmosClientFactory.LooksLikeConnectionString(options.Endpoint)
            ? null
            : await secretProvider.GetCosmosKeyAsync(cancellationToken);

        try
        {
            using var client = CosmosClientFactory.Create(options.Endpoint, key);

            var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(
                options.DatabaseName,
                cancellationToken: cancellationToken);
            LogResource("database", options.DatabaseName, databaseResponse.StatusCode);

            var database = databaseResponse.Database;

            foreach (var spec in BuildSpecs(options))
            {
                if (string.IsNullOrWhiteSpace(spec.Name))
                {
                    logger.LogWarning(
                        "Cosmos container spec has empty name (partition {Partition}); skipping.",
                        spec.PartitionKeyPath);
                    continue;
                }

                var containerResponse = await database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(spec.Name, spec.PartitionKeyPath),
                    cancellationToken: cancellationToken);

                LogResource(
                    $"container (partition {spec.PartitionKeyPath})",
                    spec.Name,
                    containerResponse.StatusCode);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cosmos bootstrap failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void LogResource(string kind, string name, HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.Created)
        {
            logger.LogInformation("Cosmos {Kind} '{Name}' created.", kind, name);
        }
        else
        {
            logger.LogInformation("Cosmos {Kind} '{Name}' already exists.", kind, name);
        }
    }

    /// <summary>
    /// Single source of truth for which Cosmos containers the API depends on.
    /// Add a yield return as each new feature comes online.
    /// </summary>
    private static IEnumerable<CosmosContainerSpec> BuildSpecs(CosmosOptions options)
    {
        yield return new CosmosContainerSpec(
            options.Containers.ProviderServices,
            ProviderServiceDocument.PartitionKeyPath);

        yield return new CosmosContainerSpec(
            options.Containers.Events,
            EventDocument.PartitionKeyPath);

        // Future containers (uncomment as their features land):
        // yield return new CosmosContainerSpec(options.Containers.PetProfiles,        "/customerId");
        // yield return new CosmosContainerSpec(options.Containers.VisitNotes,         "/providerId");
        // yield return new CosmosContainerSpec(options.Containers.ProviderDocuments,  "/providerId");
    }
}

using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.ProviderServices;
using Pawfront.Infrastructure.Cosmos.Documents;

namespace Pawfront.Infrastructure.Cosmos.ProviderServices;

/// <summary>
/// Category-agnostic operations on the shared <c>ProviderServices</c> container.
/// The five per-category registries own the document shapes; this only needs the
/// keys (doc id = ProviderId, partition key = serviceCategory).
/// </summary>
internal sealed class CosmosProviderServiceStore(
    IProviderServicesContainerAccessor containerAccessor) : IProviderServiceCosmosStore
{
    public async Task DeleteAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);

        try
        {
            await container.DeleteItemAsync<ProviderServiceDocument>(
                providerId.ToString(),
                new PartitionKey(serviceCategory),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Registered but never completed an offering — no document to remove.
        }
    }
}

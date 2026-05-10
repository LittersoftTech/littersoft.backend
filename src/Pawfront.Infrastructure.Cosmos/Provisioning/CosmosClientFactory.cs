using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Pawfront.Infrastructure.Cosmos.Provisioning;

internal static class CosmosClientFactory
{
    public static CosmosClient Create(string endpointOrConnectionString, string? key)
    {
        if (string.IsNullOrWhiteSpace(endpointOrConnectionString))
        {
            throw new InvalidOperationException("Cosmos:Endpoint is required.");
        }

        var clientOptions = new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        };

        if (LooksLikeConnectionString(endpointOrConnectionString))
        {
            return new CosmosClient(endpointOrConnectionString, clientOptions);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Cosmos endpoint requires a key. Either supply Cosmos:Key (or its secret) " +
                "or put a full connection string into Cosmos:Endpoint.");
        }

        return new CosmosClient(endpointOrConnectionString, key, clientOptions);
    }

    public static bool LooksLikeConnectionString(string value)
    {
        return value.Contains("AccountEndpoint=", StringComparison.OrdinalIgnoreCase);
    }
}

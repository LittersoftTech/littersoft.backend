namespace Pawfront.Infrastructure.Cosmos;

public sealed class CosmosOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string KeySecretName { get; init; } = "CosmosKey";
    public string DatabaseName { get; init; } = "pawfront";
    public CosmosContainerOptions Containers { get; init; } = new();
}

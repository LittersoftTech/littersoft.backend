namespace Pawfront.Infrastructure.Cosmos.Provisioning;

public sealed record CosmosContainerSpec(string Name, string PartitionKeyPath);

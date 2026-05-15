namespace Pawfront.Infrastructure.Cosmos;

public sealed class CosmosContainerOptions
{
    public string PetProfiles { get; init; } = "pet-profiles";
    public string VisitNotes { get; init; } = "visit-notes";
    public string ProviderDocuments { get; init; } = "provider-documents";
    public string ProviderServices { get; init; } = "ProviderServices";
    public string Events { get; init; } = "Events";
}

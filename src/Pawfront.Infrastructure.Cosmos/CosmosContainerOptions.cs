namespace Pawfront.Infrastructure.Cosmos;

public sealed class CosmosContainerOptions
{
    public string PetProfiles { get; init; } = "pet-profiles";
    public string VisitNotes { get; init; } = "visit-notes";
    public string ProviderDocuments { get; init; } = "provider-documents";
    public string ProviderServices { get; init; } = "ProviderServices";
    public string Events { get; init; } = "Events";

    /// <summary>
    /// Chat message bodies, partitioned by <c>/conversationId</c>. SQL owns the
    /// thread index and the counters; this holds the volume.
    /// </summary>
    public string ChatMessages { get; init; } = "ChatMessages";
}

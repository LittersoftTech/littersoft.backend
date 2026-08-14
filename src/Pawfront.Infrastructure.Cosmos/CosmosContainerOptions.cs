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

    /// <summary>
    /// Support ticket narratives — the reporter's account and the clarification thread —
    /// partitioned by <c>/ticketId</c>. SQL owns the ticket row and every rule that has to
    /// be a T-SQL predicate; this holds the words, which can grow without bound.
    /// </summary>
    public string SupportTickets { get; init; } = "SupportTickets";
}

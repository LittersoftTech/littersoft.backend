namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed record VisitNoteDocument(
    string Id,
    string ProviderId,
    string BookingId,
    string PetId,
    string Note,
    IReadOnlyCollection<string> AttachmentIds,
    DateTimeOffset CreatedAt);

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed record PetProfileDocument(
    string Id,
    string CustomerId,
    string PetId,
    string Name,
    IReadOnlyCollection<string> Preferences,
    IReadOnlyCollection<string> MedicalNotes,
    DateTimeOffset UpdatedAt);

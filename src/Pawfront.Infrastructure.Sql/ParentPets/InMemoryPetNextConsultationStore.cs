using Pawfront.Application.ParentPets;

namespace Pawfront.Infrastructure.Sql.ParentPets;

/// <summary>
/// Dev fallback (no SQL configured). The in-memory booking store has no pet
/// records to attach a consultation to, so writes are accepted and dropped —
/// same posture as the other in-memory placeholders.
/// </summary>
internal sealed class InMemoryPetNextConsultationStore : IPetNextConsultationStore
{
    public Task UpsertAsync(
        Guid petId,
        string consultationType,
        DateOnly nextConsultationDate,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

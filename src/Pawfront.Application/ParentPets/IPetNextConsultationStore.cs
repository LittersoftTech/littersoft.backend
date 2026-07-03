namespace Pawfront.Application.ParentPets;

/// <summary>
/// Persists a pet's next-consultation dates — one row per provider type
/// (Groomer | Vet | Trainer); a newer date from the same type replaces the old
/// one. Written by the provider's booking-complete flow; read back through the
/// pet endpoints (result set 3 of Parent.GetPetParentPet / ListPetParentPets),
/// not through this interface.
/// </summary>
public interface IPetNextConsultationStore
{
    /// <summary>
    /// Inserts or replaces the pet's next-consultation date for one provider
    /// type. Throws <see cref="PetNotFoundException"/> when the pet is missing.
    /// </summary>
    Task UpsertAsync(
        Guid petId,
        string consultationType,
        DateOnly nextConsultationDate,
        CancellationToken cancellationToken);
}

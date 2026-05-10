namespace Pawfront.Application.Services.Vet;

public interface IVetServiceRegistry
{
    Task<VetServiceResult> RegisterVetClinicAsync(
        RegisterVetClinicCommand command,
        CancellationToken cancellationToken);

    Task<VetServiceResult> RegisterFreelanceVeterinarianAsync(
        RegisterFreelanceVeterinarianCommand command,
        CancellationToken cancellationToken);

    Task<VetServiceResult> SaveVetClinicOfferingAsync(
        SaveVetClinicOfferingCommand command,
        CancellationToken cancellationToken);

    Task<VetServiceResult> SaveFreelanceVeterinarianOfferingAsync(
        SaveFreelanceVeterinarianOfferingCommand command,
        CancellationToken cancellationToken);

    Task<VetServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

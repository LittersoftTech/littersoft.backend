namespace Pawfront.Application.Services.PetGroomer;

public interface IPetGroomerServiceRegistry
{
    Task<PetGroomerServiceResult> RegisterGroomerShopAsync(
        RegisterGroomerShopCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> RegisterFreelanceGroomerAsync(
        RegisterFreelanceGroomerCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> SaveGroomerShopOfferingAsync(
        SaveGroomerShopOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> SaveFreelanceGroomerOfferingAsync(
        SaveFreelanceGroomerOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

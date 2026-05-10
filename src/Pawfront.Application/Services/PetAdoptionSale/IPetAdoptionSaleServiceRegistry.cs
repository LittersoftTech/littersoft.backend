namespace Pawfront.Application.Services.PetAdoptionSale;

public interface IPetAdoptionSaleServiceRegistry
{
    Task<PetAdoptionSaleServiceResult> RegisterPetShelterAsync(
        RegisterPetShelterCommand command,
        CancellationToken cancellationToken);

    Task<PetAdoptionSaleServiceResult> RegisterPetShopAsync(
        RegisterPetShopCommand command,
        CancellationToken cancellationToken);

    Task<PetAdoptionSaleServiceResult> RegisterFreelanceAsync(
        RegisterFreelancePetAdoptionSaleCommand command,
        CancellationToken cancellationToken);

    Task<PetAdoptionSaleServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

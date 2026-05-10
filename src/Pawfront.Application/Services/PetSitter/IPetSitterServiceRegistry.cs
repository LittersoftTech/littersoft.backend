namespace Pawfront.Application.Services.PetSitter;

public interface IPetSitterServiceRegistry
{
    Task<PetSitterServiceResult> RegisterPetHotelAsync(
        RegisterPetHotelCommand command,
        CancellationToken cancellationToken);

    Task<PetSitterServiceResult> RegisterFreelancePetSitterAsync(
        RegisterFreelancePetSitterCommand command,
        CancellationToken cancellationToken);

    Task<PetSitterServiceResult> SavePetHotelOfferingAsync(
        SavePetHotelOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetSitterServiceResult> SaveFreelancePetSitterOfferingAsync(
        SaveFreelancePetSitterOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetSitterServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

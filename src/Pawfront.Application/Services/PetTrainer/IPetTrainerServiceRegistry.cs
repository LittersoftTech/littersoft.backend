namespace Pawfront.Application.Services.PetTrainer;

public interface IPetTrainerServiceRegistry
{
    Task<PetTrainerServiceResult> RegisterTrainingSchoolAsync(
        RegisterTrainingSchoolCommand command,
        CancellationToken cancellationToken);

    Task<PetTrainerServiceResult> RegisterFreelanceTrainerAsync(
        RegisterFreelanceTrainerCommand command,
        CancellationToken cancellationToken);

    Task<PetTrainerServiceResult> SaveTrainingSchoolOfferingAsync(
        SaveTrainingSchoolOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetTrainerServiceResult> SaveFreelanceTrainerOfferingAsync(
        SaveFreelanceTrainerOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetTrainerServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

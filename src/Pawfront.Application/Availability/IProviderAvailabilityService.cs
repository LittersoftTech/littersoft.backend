namespace Pawfront.Application.Availability;

public interface IProviderAvailabilityService
{
    Task<ProviderWeeklyAvailabilityResult> SaveAsync(
        SaveProviderWeeklyAvailabilityCommand command,
        CancellationToken cancellationToken);

    Task<ProviderWeeklyAvailabilityResult> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

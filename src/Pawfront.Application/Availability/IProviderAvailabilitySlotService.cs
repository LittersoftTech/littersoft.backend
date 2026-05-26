namespace Pawfront.Application.Availability;

public interface IProviderAvailabilitySlotService
{
    Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Availability;

public interface IProviderAvailabilitySlotService
{
    Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        CancellationToken cancellationToken);
}

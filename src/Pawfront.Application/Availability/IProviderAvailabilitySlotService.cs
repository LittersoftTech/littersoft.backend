namespace Pawfront.Application.Availability;

public interface IProviderAvailabilitySlotService
{
    /// <summary>
    /// For PetGroomer queries, <paramref name="serviceItemCode"/> is required and
    /// the server resolves the slot duration from the provider's menu item
    /// (<paramref name="durationHours"/> is ignored). For every other category,
    /// duration comes from <paramref name="durationHours"/> and the code is
    /// ignored.
    /// </summary>
    Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        string? serviceItemCode,
        CancellationToken cancellationToken);
}

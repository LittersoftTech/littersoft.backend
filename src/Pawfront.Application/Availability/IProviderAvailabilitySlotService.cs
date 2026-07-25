namespace Pawfront.Application.Availability;

public interface IProviderAvailabilitySlotService
{
    /// <summary>
    /// For PetGroomer queries, <paramref name="serviceItemCode"/> is required and
    /// the server resolves the slot duration from the provider's menu item
    /// (<paramref name="durationHours"/> is ignored). For every other category,
    /// duration comes from <paramref name="durationHours"/> and the code is
    /// ignored.
    ///
    /// NightStay services are DATE-granular: <paramref name="durationHours"/> and
    /// <paramref name="granularityMinutes"/> are ignored, hourly <c>Slots</c> stay
    /// empty, and the result's <c>Nights</c> carries per-night remaining capacity
    /// for every night in [<paramref name="date"/>, <paramref name="endDate"/>]
    /// (<paramref name="endDate"/> defaults to <paramref name="date"/>; ignored
    /// for hourly services).
    /// </summary>
    Task<AvailableSlotsResult> GetAvailableSlotsAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal durationHours,
        int granularityMinutes,
        string? serviceItemCode,
        CancellationToken cancellationToken,
        DateOnly? endDate = null);
}

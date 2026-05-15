namespace Pawfront.Application.Availability;

public sealed record AvailableSlotsResult(
    Guid ProviderId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    decimal DurationHours,
    int Capacity,
    int GranularityMinutes,
    IReadOnlyCollection<TimeSlot> Slots);

public sealed record TimeSlot(TimeOnly StartTime, TimeOnly EndTime);

public sealed class ProviderServiceNotRegisteredException(Guid providerId)
    : Exception($"Provider '{providerId}' has not registered a service yet.");

public sealed class ProviderOfferingNotConfiguredException(Guid providerId, string serviceCategory)
    : Exception($"Provider '{providerId}' has no offering details configured for '{serviceCategory}'.");

public sealed class InvalidBookingDurationException(string message) : Exception(message);

namespace Pawfront.Application.Availability;

public sealed record AvailableSlotsResult(
    Guid ProviderId,
    Guid ServiceId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    decimal DurationHours,
    int Capacity,
    int GranularityMinutes,
    IReadOnlyCollection<TimeSlot> Slots);

public sealed record TimeSlot(TimeOnly StartTime, TimeOnly EndTime);

public sealed class SlotServiceInvalidException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not valid or active for provider '{providerId}'.");

public sealed class ProviderServiceNotRegisteredException(Guid providerId)
    : Exception($"Provider '{providerId}' has not registered a service yet.");

public sealed class ProviderOfferingNotConfiguredException(Guid providerId, string serviceCategory)
    : Exception($"Provider '{providerId}' has no offering details configured for '{serviceCategory}'.");

public sealed class InvalidBookingDurationException(string message) : Exception(message);

public sealed class SlotGroomingItemCodeRequiredException()
    : Exception("A grooming serviceItemCode is required when querying Pet Groomer slots.");

public sealed class SlotGroomingItemNotOfferedException(Guid providerId, string code)
    : Exception($"Provider '{providerId}' does not offer grooming service '{code}'.");

public sealed class SlotGroomingItemInactiveException(Guid providerId, string code)
    : Exception($"Grooming service '{code}' is currently disabled for provider '{providerId}'.");

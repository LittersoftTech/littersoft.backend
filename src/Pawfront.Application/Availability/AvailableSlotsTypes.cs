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
    IReadOnlyCollection<TimeSlot> Slots,
    // NightStay is date-granular: instead of hourly Slots (always empty for
    // NightStay) the result carries one entry per night in [Date, EndDate]
    // with the night's remaining capacity. Null for every other service type.
    IReadOnlyCollection<NightAvailability>? Nights = null);

/// <summary>
/// One slot window. <see cref="RemainingCapacity"/> = the offering's
/// capacity minus the active bookings overlapping this window — how many more
/// pets can still be booked into it. Fully-booked slots ARE emitted with
/// <see cref="RemainingCapacity"/> = 0 so clients can render them as
/// unavailable; only slots with a positive remaining capacity are bookable.
/// </summary>
public sealed record TimeSlot(TimeOnly StartTime, TimeOnly EndTime, int RemainingCapacity);

/// <summary>
/// Per-night availability for a NightStay service. A night is unavailable when
/// a full-day closure covers it (<see cref="IsClosed"/>) or every capacity unit
/// is taken by an active stay covering it (CheckInDate &lt;= night &lt; CheckOutDate).
/// </summary>
public sealed record NightAvailability(
    DateOnly Date,
    int ActiveBookings,
    int RemainingCapacity,
    bool IsClosed,
    bool IsAvailable);

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

namespace Pawfront.Application.Bookings;

public sealed record CreateBookingCommand(
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode);

/// <summary>
/// Provider-initiated private/custom booking for an unregistered walk-in
/// customer. Shares the booking scheduling model (same per-service capacity
/// bucket, same closure / availability / active-status gating) but carries the
/// customer details inline as free text instead of a PetParentId.
/// </summary>
public sealed record CreateCustomBookingCommand(
    Guid ProviderId,
    Guid ServiceId,
    string CustomerName,
    string CustomerMobileCountryCode,
    string CustomerMobile,
    string AnimalType,
    string PetName,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string ServiceLocation,
    string? CustomerLocation,
    decimal PricePerHour,
    string? JobNotes);

public sealed record BookingResult(
    Guid BookingId,
    Guid ProviderId,
    // Null for Source = 'Custom' bookings (provider-added walk-ins).
    Guid? PetParentId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    string? ServiceItemCode,
    // 'App' (registered pet parent booked via the consumer app) or 'Custom'
    // (provider added a private/walk-in job). Discriminates which of the
    // PetParentId / Customer* fields below carries the identity.
    string Source,
    // Custom-job fields — populated only when Source = 'Custom'.
    string? CustomerName,
    string? CustomerMobileCountryCode,
    string? CustomerMobile,
    string? AnimalType,
    string? PetName,
    string? ServiceLocation,
    string? CustomerLocation,
    decimal? PricePerHour,
    string? JobNotes);

/// <summary>Lightweight pair used by the slot service to subtract overlaps.</summary>
public sealed record BookingWindow(TimeOnly StartTime, TimeOnly EndTime);

public sealed class BookingProviderNotRegisteredException(Guid providerId)
    : Exception($"Provider '{providerId}' has not registered a service yet.");

public sealed class BookingOfferingNotConfiguredException(Guid providerId, string serviceCategory)
    : Exception($"Provider '{providerId}' has no offering configured for '{serviceCategory}'.");

public sealed class BookingServiceInvalidException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not valid or active for provider '{providerId}'.");

public sealed class BookingPetParentNotFoundException(Guid petParentId)
    : Exception($"Pet parent '{petParentId}' was not found.");

public sealed class BookingProviderNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' was not found.");

public sealed class BookingNotFoundException(Guid bookingId)
    : Exception($"Booking '{bookingId}' was not found.");

public sealed class BookingCapacityExceededException(Guid serviceId, DateOnly date, TimeOnly startTime, TimeOnly endTime)
    : Exception($"Service '{serviceId}' has no remaining capacity for {date} {startTime}-{endTime}.");

public sealed class BookingCancellationForbiddenException(Guid bookingId)
    : Exception($"Only the original booker can cancel booking '{bookingId}'.");

public sealed class BookingAlreadyCancelledException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is already cancelled.");

public sealed class InvalidBookingTimeException(string message) : Exception(message);

public sealed class BookingProviderInactiveException(Guid providerId)
    : Exception($"Provider '{providerId}' is currently inactive and is not accepting new bookings.");

public sealed class BookingGroomingItemCodeRequiredException()
    : Exception("A grooming service code (serviceItemCode) is required when booking a Pet Groomer.");

public sealed class BookingGroomingItemNotOfferedException(Guid providerId, string code)
    : Exception($"Provider '{providerId}' does not offer grooming service '{code}'.");

public sealed class BookingGroomingItemInactiveException(Guid providerId, string code)
    : Exception($"Grooming service '{code}' is currently disabled for provider '{providerId}'.");

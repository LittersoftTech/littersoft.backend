namespace Pawfront.Application.Bookings;

public sealed record CreateBookingCommand(
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record BookingResult(
    Guid BookingId,
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

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

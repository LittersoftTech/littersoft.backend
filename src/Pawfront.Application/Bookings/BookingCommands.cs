namespace Pawfront.Application.Bookings;

public sealed record CreateBookingCommand(
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode,
    // Which of the parent's pets the booking is for. Optional — the provider
    // host's booking flow doesn't capture it; the parent host's does. Ownership
    // (pet belongs to PetParentId) is validated by the caller AND the sproc.
    Guid? PetId = null);

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
    string? JobNotes,
    // Which of the parent's pets the booking is for. Null for Custom
    // walk-ins and for legacy/provider-host bookings.
    Guid? PetId = null);

/// <summary>Lightweight pair used by the slot service to subtract overlaps.</summary>
public sealed record BookingWindow(TimeOnly StartTime, TimeOnly EndTime);

/// <summary>Which party is driving a booking status change.</summary>
public enum BookingStatusActor
{
    Provider,
    Parent
}

/// <summary>
/// Request to move a booking to a new lifecycle status. <see cref="ActorId"/> is
/// the caller's ProviderId (when <see cref="Actor"/> is
/// <see cref="BookingStatusActor.Provider"/>) or PetParentId (when it is
/// <see cref="BookingStatusActor.Parent"/>) — derived from the authenticated
/// route, never the body. The sproc enforces that the actor is party to the
/// booking and that the transition is permitted for that actor.
/// </summary>
public sealed record UpdateBookingStatusCommand(
    Guid BookingId,
    string NewStatus,
    BookingStatusActor Actor,
    Guid ActorId,
    string? Note);

/// <summary>One audited booking status change (or the seeded creation entry).</summary>
public sealed record BookingStatusHistoryEntry(
    Guid BookingStatusHistoryId,
    Guid BookingId,
    // Null only for the initial creation entry (no prior status).
    string? FromStatus,
    string ToStatus,
    // 'Provider', 'Parent', or 'System' (the creation seed).
    string ChangedByActor,
    // The ProviderId / PetParentId behind the change; null for System entries.
    Guid? ChangedByActorId,
    string? Note,
    DateTimeOffset ChangedAtUtc);

public sealed class BookingProviderNotRegisteredException(Guid providerId)
    : Exception($"Provider '{providerId}' has not registered a service yet.");

public sealed class BookingOfferingNotConfiguredException(Guid providerId, string serviceCategory)
    : Exception($"Provider '{providerId}' has no offering configured for '{serviceCategory}'.");

public sealed class BookingServiceInvalidException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not valid or active for provider '{providerId}'.");

public sealed class BookingPetParentNotFoundException(Guid petParentId)
    : Exception($"Pet parent '{petParentId}' was not found.");

public sealed class BookingPetInvalidException(Guid petId, Guid petParentId)
    : Exception($"Pet '{petId}' was not found or does not belong to pet parent '{petParentId}'.");

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

public sealed class UnsupportedBookingStatusException(string status)
    : Exception($"Booking status '{status}' is not supported.");

/// <summary>The caller is not the provider/parent on the booking they're trying to change.</summary>
public sealed class BookingStatusForbiddenException(Guid bookingId)
    : Exception($"You are not a party to booking '{bookingId}' and cannot change its status.");

/// <summary>The requested status is not one this actor is allowed to set.</summary>
public sealed class BookingStatusNotAllowedException(string status, BookingStatusActor actor)
    : Exception($"A {actor} cannot set booking status '{status}'.");

/// <summary>The booking is already in a terminal status and can't change further.</summary>
public sealed class BookingStatusTerminalException(Guid bookingId, string currentStatus)
    : Exception($"Booking '{bookingId}' is in terminal status '{currentStatus}' and cannot change.");

/// <summary>The booking is already in the requested status.</summary>
public sealed class BookingStatusUnchangedException(Guid bookingId, string status)
    : Exception($"Booking '{bookingId}' is already in status '{status}'.");

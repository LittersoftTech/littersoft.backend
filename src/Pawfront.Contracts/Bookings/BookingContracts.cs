namespace Pawfront.Contracts.Bookings;

public sealed record CreateBookingRequest(
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode);

/// <summary>
/// Body for <c>POST /pet-parents/{petParentId}/bookings</c> on the pet-parent
/// host. The booker is the route's petParentId (ownership-filtered); PetId
/// must be one of their pets. The provider is resolved server-side from
/// ServiceId. ServiceItemCode is required for PetGroomer services only.
/// </summary>
public sealed record CreateParentBookingRequest(
    Guid PetId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode);

/// <summary>
/// Provider-initiated private/custom booking for an unregistered walk-in.
/// Counts against the same per-service capacity bucket as app bookings.
/// </summary>
public sealed record CreateCustomBookingRequest(
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

public sealed record CancelBookingRequest(Guid PetParentId);

/// <summary>
/// Body for the booking status-change endpoints
/// (<c>POST /providers/{providerId}/bookings/{bookingId}/status</c> and
/// <c>POST /pet-parents/{petParentId}/bookings/{bookingId}/status</c>). The
/// acting party (Provider/Parent) and its id come from the authenticated route,
/// not the body. <c>Status</c> is one of CREATED, CONFIRMED, COMPLETED,
/// APPROVAL_NEEDED, PROVIDER_CANCELLED, PARENT_CANCELLED — though each host only
/// permits the subset valid for its actor. <c>Note</c> is an optional free-text
/// reason captured on the audit row.
/// </summary>
public sealed record UpdateBookingStatusRequest(string Status, string? Note);

/// <summary>One row of a booking's status-change audit trail.</summary>
public sealed record BookingStatusHistoryEntryResponse(
    Guid BookingStatusHistoryId,
    Guid BookingId,
    string? FromStatus,
    string ToStatus,
    string ChangedByActor,
    Guid? ChangedByActorId,
    string? Note,
    DateTimeOffset ChangedAtUtc);

public sealed record BookingResponse(
    Guid BookingId,
    Guid ProviderId,
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
    string Source,
    string? CustomerName,
    string? CustomerMobileCountryCode,
    string? CustomerMobile,
    string? AnimalType,
    string? PetName,
    string? ServiceLocation,
    string? CustomerLocation,
    decimal? PricePerHour,
    string? JobNotes,
    // Which of the parent's pets the booking is for; null for Custom
    // walk-ins and legacy rows.
    Guid? PetId = null);

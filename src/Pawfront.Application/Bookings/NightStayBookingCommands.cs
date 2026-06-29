namespace Pawfront.Application.Bookings;

/// <summary>
/// Request to book a multi-night boarding stay (PetSitter NightStay service).
/// Distinct from <see cref="CreateBookingCommand"/>, which is single-day. The
/// stay spans <c>[CheckInDate, CheckOutDate)</c> — the checkout day is NOT a
/// stayed night. Drop-off / pick-up times are resolved server-side from the
/// provider's offering, not supplied by the caller.
/// </summary>
public sealed record CreateNightStayBookingCommand(
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    // Which of the parent's pets the stay is for. Optional at this layer — a
    // future provider-host flow may omit it; the parent host always supplies it.
    // Ownership is validated by the caller AND the sproc.
    Guid? PetId = null);

public sealed record NightStayBookingResult(
    Guid NightStayBookingId,
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    Guid? PetId);

/// <summary>
/// Raw enriched night-stay booking row backing the detail read
/// (<c>Booking.GetNightStayBookingDetail</c>). Mirrors <see cref="BookingDetailRow"/>
/// for the multi-night model: base columns plus the sequential <see cref="JobNumber"/>,
/// the capture-only payout fields, and the LEFT-JOINed pet-parent / pet records.
/// Night-stay is App-only, so the parent/pet details always come from the joins.
/// </summary>
public sealed record NightStayBookingDetailRow(
    Guid NightStayBookingId,
    int JobNumber,
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    Guid? PetId,
    string PayoutStatus,
    string? PayoutId,
    // Pet-parent join.
    string? ParentFirstName,
    string? ParentLastName,
    string? ParentGender,
    string? ParentMobileCountryCode,
    string? ParentMobileNumber,
    string? ParentPhotoUrl,
    // Pet join.
    string? PetProfileName,
    string? PetType,
    string? PetGender,
    string? PetPhotoUrl);

/// <summary>
/// Fully resolved night-stay booking-detail view: the raw <see cref="Row"/> plus the
/// friendly Job ID and live-computed payment figures. <see cref="PricePerNight"/> is
/// the NightStay offering's per-night rate; <see cref="TotalAmount"/> is
/// rate × <see cref="Nights"/>; <see cref="PawfrontFee"/> is <see cref="FeePercentage"/>
/// percent of the total. Pricing/location fields are null when the offering can't be
/// resolved. Mapped to the sectioned response in the endpoint layer.
/// </summary>
public sealed record NightStayBookingDetailResult(
    NightStayBookingDetailRow Row,
    string JobId,
    int Nights,
    decimal? PricePerNight,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage,
    string? ServiceLocation,
    int? MinimumHoursBeforeCancellation);

/// <summary>
/// Request to move a night-stay booking to a new lifecycle status. Same shape
/// and actor rules as <see cref="UpdateBookingStatusCommand"/>; <see cref="ActorId"/>
/// is the caller's ProviderId / PetParentId derived from the authenticated route.
/// </summary>
public sealed record UpdateNightStayBookingStatusCommand(
    Guid NightStayBookingId,
    string NewStatus,
    BookingStatusActor Actor,
    Guid ActorId,
    string? Note);

/// <summary>
/// Either party proposes a new check-in / check-out range for a night-stay
/// booking. The start (<see cref="StartBookingCommand"/>) and respond
/// (<see cref="RespondBookingModificationCommand"/>) commands are shared with the
/// single-day flow — only the proposed fields differ, so the request has its own
/// command.
/// </summary>
public sealed record RequestNightStayBookingModificationCommand(
    Guid NightStayBookingId,
    BookingStatusActor Actor,
    Guid ActorId,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    string? Note);

/// <summary>
/// The staged (pending) check-in/check-out change proposal for a night-stay
/// booking. Removed from staging once accepted or declined.
/// </summary>
public sealed record NightStayBookingModificationResult(
    Guid NightStayBookingModificationId,
    Guid NightStayBookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedCheckInDate,
    DateOnly ProposedCheckOutDate,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>The requested service id is not a NightStay service of this provider.</summary>
public sealed class BookingNotNightStayServiceException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not a NightStay service for provider '{providerId}'.");

/// <summary>Check-in / check-out dates are invalid (order or span out of range).</summary>
public sealed class InvalidNightStayDatesException(string message) : Exception(message);

/// <summary>One or more nights in the requested stay have no remaining capacity.</summary>
public sealed class NightStayCapacityExceededException(Guid serviceId, DateOnly checkInDate, DateOnly checkOutDate)
    : Exception($"Service '{serviceId}' has no remaining capacity for one or more nights between {checkInDate:yyyy-MM-dd} and {checkOutDate:yyyy-MM-dd}.");

public sealed class NightStayBookingNotFoundException(Guid bookingId)
    : Exception($"Night stay booking '{bookingId}' was not found.");

public sealed class NightStayBookingCancellationForbiddenException(Guid bookingId)
    : Exception($"Only the original booker can cancel night stay booking '{bookingId}'.");

public sealed class NightStayBookingAlreadyCancelledException(Guid bookingId)
    : Exception($"Night stay booking '{bookingId}' is already cancelled.");

/// <summary>
/// Thrown by the single-day booking path when a NightStay service id is used —
/// callers must use the dedicated night-stay booking endpoint instead.
/// </summary>
public sealed class BookingNightStayUseDedicatedEndpointException()
    : Exception("This is a NightStay service. Use the night-stay booking endpoint (POST .../night-stay-bookings) with checkInDate and checkOutDate.");

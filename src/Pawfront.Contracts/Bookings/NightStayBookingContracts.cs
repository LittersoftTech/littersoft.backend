namespace Pawfront.Contracts.Bookings;

/// <summary>
/// Body for the parent-host night-stay create
/// (<c>POST /pet-parents/{petParentId}/night-stay-bookings</c>). The booker is the
/// route's petParentId (ownership-filtered); PetId must be one of their pets. The
/// provider is resolved server-side from ServiceId. The stay spans
/// <c>[CheckInDate, CheckOutDate)</c> — CheckOutDate is the pickup day and is NOT
/// a stayed night.
/// </summary>
public sealed record CreateParentNightStayBookingRequest(
    Guid PetId,
    Guid ServiceId,
    DateOnly CheckInDate,
    DateOnly CheckOutDate);

public sealed record NightStayBookingResponse(
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
/// Body for a night-stay modification request
/// (<c>POST .../night-stay-bookings/{bookingId}/modifications</c>). The proposed
/// check-in / check-out range. Accept/decline reuses
/// <see cref="RespondBookingModificationRequest"/>.
/// </summary>
public sealed record RequestNightStayBookingModificationRequest(
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    string? Note);

/// <summary>The staged (pending) check-in/check-out change proposal on a night-stay booking.</summary>
public sealed record NightStayBookingModificationResponse(
    Guid NightStayBookingModificationId,
    Guid NightStayBookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedCheckInDate,
    DateOnly ProposedCheckOutDate,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Night-stay single booking read, grouped into the same sections as the single-day
/// <see cref="BookingDetailResponse"/> — Booking, Parent, Pet, Payment, and the
/// provider's Cancellation Policy — plus the start-OTP (when startable) and the
/// staged pending modification (when one awaits a response). Night-stay is App-only,
/// so Parent/Pet always come from the joined records.
/// </summary>
public sealed record NightStayBookingDetailResponse(
    NightStayBookingDetailsSection BookingDetails,
    ParentDetailsSection ParentDetails,
    PetDetailsSection PetDetails,
    NightStayPaymentDetailsSection PaymentDetails,
    CancellationPolicyDetailsSection CancellationPolicy,
    StartOtpResponse? StartOtp,
    NightStayBookingModificationResponse? PendingModification);

/// <summary>The stay/job facts: identity, the check-in/check-out range + nights,
/// drop-off/pick-up times, status, and where the provider delivers the service.</summary>
public sealed record NightStayBookingDetailsSection(
    Guid NightStayBookingId,
    // Short, human-friendly sequential Job ID, e.g. "PF-000123".
    string JobId,
    Guid ProviderId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime,
    // Stayed nights = CheckOutDate - CheckInDate (the checkout day isn't a night).
    int Nights,
    string Status,
    // The provider offering's service-location setting; null when unresolved.
    string? ServiceLocation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

/// <summary>The money facts for a night stay. <c>PricePerNight</c> is the offering's
/// per-night rate; <c>TotalAmount</c> is rate × nights; <c>PawfrontFee</c> is
/// <c>FeePercentage</c> percent of the total. Pricing is null when the offering can't
/// be resolved. Payout fields are capture-only for now.</summary>
public sealed record NightStayPaymentDetailsSection(
    decimal? PricePerNight,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage,
    string PayoutStatus,
    string? PayoutId);

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

/// <summary>Body for <c>POST .../bookings/{bookingId}/start</c> (provider).</summary>
public sealed record StartBookingRequest(string OtpCode);

/// <summary>
/// The parent-facing start-OTP block, surfaced on the parent's booking-details
/// read when the booking is startable. The code is read to the provider.
/// </summary>
public sealed record StartOtpResponse(string Code, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Body for a single-day modification request
/// (<c>POST .../bookings/{bookingId}/modifications</c>). Editing is limited to the
/// schedule — date + time window only.
/// </summary>
public sealed record RequestBookingModificationRequest(
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Note);

/// <summary>
/// Body for accepting/declining a modification
/// (<c>POST .../modifications/accept</c> | <c>/decline</c>). Accept-vs-decline is
/// the route, not the body; only an optional note travels here.
/// </summary>
public sealed record RespondBookingModificationRequest(string? Note);

/// <summary>
/// The staged (pending) date/time-change proposal on a single-day booking, so the
/// counterparty can see what's proposed before accepting/declining.
/// </summary>
public sealed record BookingModificationResponse(
    Guid BookingModificationId,
    Guid BookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedBookingDate,
    TimeOnly ProposedStartTime,
    TimeOnly ProposedEndTime,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>One job-completion evidence photo.</summary>
public sealed record BookingEvidenceResponse(
    Guid BookingEvidenceId,
    Guid BookingId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Single booking-detail read, grouped into four sections — Booking, Parent, Pet,
/// and Payment — plus the start-OTP (populated only when the booking is in a
/// startable state — parent reads only, otherwise null) and the staged pending
/// modification (populated only while a proposal awaits a response, otherwise
/// null). For App bookings the Parent/Pet sections are filled from the joined
/// pet-parent + pet records; for Custom walk-ins they come from the booking's own
/// free-text fields (and parent/pet photos + genders are null).
/// </summary>
public sealed record BookingDetailResponse(
    BookingDetailsSection BookingDetails,
    ParentDetailsSection ParentDetails,
    PetDetailsSection PetDetails,
    PaymentDetailsSection PaymentDetails,
    StartOtpResponse? StartOtp,
    BookingModificationResponse? PendingModification);

/// <summary>The booking/job facts: identity, schedule, status, and (Custom-only)
/// service-location + notes.</summary>
public sealed record BookingDetailsSection(
    Guid BookingId,
    // Short, human-friendly sequential Job ID, e.g. "PF-000123".
    string JobId,
    Guid ProviderId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Status,
    // 'App' (registered pet parent) or 'Custom' (provider walk-in).
    string Source,
    // Custom walk-ins only; null for App bookings.
    string? ServiceLocation,
    string? CustomerLocation,
    string? JobNotes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);

/// <summary>The customer (pet parent) facts. For App bookings, name/mobile/gender/
/// photo come from the joined pet-parent record; for Custom walk-ins, name + mobile
/// come from the booking and the rest are null.</summary>
public sealed record ParentDetailsSection(
    Guid? PetParentId,
    string? CustomerName,
    string? CustomerMobileCountryCode,
    string? CustomerMobile,
    string? ParentGender,
    string? CustomerPhotoUrl);

/// <summary>The pet facts. For App bookings these come from the joined pet record;
/// for Custom walk-ins, petName + animalType come from the booking and the rest are
/// null.</summary>
public sealed record PetDetailsSection(
    Guid? PetId,
    string? PetName,
    string? AnimalType,
    string? PetGender,
    string? PetImageUrl);

/// <summary>The money facts. <c>PricePerHour</c> is the offering's unit rate (the
/// stored per-hour price for Custom walk-ins); <c>TotalAmount</c> is rate × time;
/// <c>PawfrontFee</c> is <c>FeePercentage</c> percent of the total. Pricing fields
/// are null when the provider's offering can't be resolved (e.g. deactivated
/// service). Payout fields are capture-only for now.</summary>
public sealed record PaymentDetailsSection(
    decimal? PricePerHour,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage,
    string PayoutStatus,
    string? PayoutId);

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

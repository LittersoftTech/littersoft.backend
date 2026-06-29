namespace Pawfront.Application.Bookings;

public sealed record CreateBookingCommand(
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode,
    // Free-text notes the parent attaches to the job (access instructions, the
    // pet's quirks, etc.). Optional; captured at create time and surfaced on the
    // booking-detail read.
    string? JobNotes = null,
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

/// <summary>
/// Raw enriched booking row backing the booking-detail read (<c>Booking.GetBookingDetail</c>).
/// Carries the base booking columns plus the sequential <see cref="JobNumber"/>,
/// the capture-only payout fields, and the LEFT-JOINed pet-parent / pet records.
/// For Custom walk-in bookings the parent/pet join fields are null (no
/// PetParentId/PetId), and the customer/pet details live on the booking row's own
/// Customer*/AnimalType/PetName columns instead. Pricing/fee totals are NOT here —
/// they're computed live by <see cref="IBookingService.GetDetailAsync"/>.
/// </summary>
public sealed record BookingDetailRow(
    Guid BookingId,
    int JobNumber,
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
    // Booking-row customer/pet fields — populated for Custom walk-ins only.
    string? CustomerName,
    string? CustomerMobileCountryCode,
    string? CustomerMobile,
    string? AnimalType,
    string? PetName,
    string? ServiceLocation,
    string? CustomerLocation,
    decimal? PricePerHour,
    string? JobNotes,
    Guid? PetId,
    string PayoutStatus,
    string? PayoutId,
    // Pet-parent join (App bookings) — null for Custom rows.
    string? ParentFirstName,
    string? ParentLastName,
    string? ParentGender,
    string? ParentMobileCountryCode,
    string? ParentMobileNumber,
    string? ParentPhotoUrl,
    // Pet join (App bookings) — null for Custom rows.
    string? PetProfileName,
    string? PetType,
    string? PetGender,
    string? PetPhotoUrl);

/// <summary>
/// Fully resolved booking-detail view: the raw <see cref="Row"/> plus the friendly
/// Job ID and the live-computed payment figures (unit price, total, Pawfront fee,
/// and the fee percentage applied). Pricing fields are null when the offering can't
/// be resolved (e.g. the service was deactivated). Mapped to the four-section
/// response in the endpoint layer.
/// </summary>
public sealed record BookingDetailResult(
    BookingDetailRow Row,
    string JobId,
    decimal? PricePerHour,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage,
    // Effective service location ("where the provider delivers the service"): the
    // Custom row's own value, or — for App bookings — the provider offering's
    // service-location setting. Null when the offering can't be resolved.
    string? ServiceLocation,
    // The provider's advertised cancellation policy (minimum hours before a
    // cancellation is allowed): null | 24 | 48 | 72 | 96. Null when none is set.
    int? MinimumHoursBeforeCancellation);

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

// --- Job lifecycle: start-OTP, evidence, modifications ----------------------

/// <summary>
/// A start-job OTP issued for a booking. The plaintext <see cref="OtpCode"/> is
/// surfaced to the parent (who reads it to the provider); the provider posts it
/// back to the start endpoint. Shared by single-day and night-stay bookings.
/// </summary>
public sealed record StartOtpResult(
    Guid BookingStartOtpId,
    Guid BookingId,
    string OtpCode,
    string Status,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>One job-completion evidence photo. Shared by single-day + night-stay.</summary>
public sealed record BookingEvidenceResult(
    Guid BookingEvidenceId,
    Guid BookingId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>Provider starts a job by entering the parent's start-OTP.</summary>
public sealed record StartBookingCommand(Guid BookingId, Guid ProviderId, string OtpCode);

/// <summary>
/// Either party proposes a new date/time for a single-day booking (editing is
/// limited to the schedule). The proposed window is validated (working hours,
/// closures, duration) before the proposal is staged; on accept capacity is
/// re-checked race-safely.
/// </summary>
public sealed record RequestBookingModificationCommand(
    Guid BookingId,
    BookingStatusActor Actor,
    Guid ActorId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Note);

/// <summary>The counterparty accepts or declines the staged modification proposal.</summary>
public sealed record RespondBookingModificationCommand(
    Guid BookingId,
    BookingStatusActor Actor,
    Guid ActorId,
    bool Accept,
    string? Note);

/// <summary>
/// The staged (pending) date/time-change proposal for a single-day booking, as
/// read back so the counterparty can see what's proposed. Removed from staging
/// once accepted (applied to the booking) or declined.
/// </summary>
public sealed record BookingModificationResult(
    Guid BookingModificationId,
    Guid BookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedBookingDate,
    TimeOnly ProposedStartTime,
    TimeOnly ProposedEndTime,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>The booking is not in a state the job can be started from.</summary>
public sealed class BookingNotStartableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is not in a state the job can be started from.");

/// <summary>The provider-entered start-OTP is missing or incorrect.</summary>
public sealed class InvalidStartOtpException(Guid bookingId)
    : Exception($"The start code for booking '{bookingId}' is missing or incorrect.");

/// <summary>The start-OTP has expired; the parent must refresh the booking.</summary>
public sealed class StartOtpExpiredException(Guid bookingId)
    : Exception($"The start code for booking '{bookingId}' has expired.");

/// <summary>A modification can only be requested on a confirmed (live) booking.</summary>
public sealed class BookingNotModifiableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is not in a state that can be modified.");

/// <summary>A modification proposal is already awaiting a response.</summary>
public sealed class BookingModificationConflictException(Guid bookingId)
    : Exception($"Booking '{bookingId}' already has a modification awaiting a response.");

/// <summary>There is no open modification proposal for this actor to respond to.</summary>
public sealed class NoPendingModificationException(Guid bookingId)
    : Exception($"Booking '{bookingId}' has no modification request awaiting your response.");

/// <summary>The proposed modification window has no remaining capacity.</summary>
public sealed class BookingModificationCapacityException(Guid bookingId)
    : Exception($"The proposed time for booking '{bookingId}' has no remaining capacity.");

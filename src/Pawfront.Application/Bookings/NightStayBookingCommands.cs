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
    Guid? PetId = null,
    // Optional free-text notes the parent attaches to the stay; surfaced on
    // the night-stay detail read.
    string? JobNotes = null,
    // Where the service is delivered: ParentLocation or ProviderLocation
    // (see <see cref="BookingLocationTypes"/>). Required on the parent host.
    string? LocationType = null);

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
/// One row of the parent's night-stay "my bookings" list: the flat booking plus
/// the frozen-at-creation extras the list cards surface — the price-locked
/// per-night rate, the cancellation-policy snapshot, and the selected-location
/// address snapshot. The location block carries ONLY the snapshot (no live
/// fallback on list reads — legacy rows show nulls; the detail read remains the
/// live-fallback authority). Mirror of <see cref="BookingListItemResult"/>.
/// </summary>
public sealed record NightStayBookingListItemResult(
    NightStayBookingResult Booking,
    decimal? PricePerNight,
    int? CancellationPolicyHours,
    BookingLocationResult Location);

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
    string? PetPhotoUrl,
    // Provider join.
    string? ProviderFirstName,
    string? ProviderLastName,
    string? ProviderGender,
    string? ProviderMobileCountryCode,
    string? ProviderMobileNumber,
    // Pet medical extras.
    string? PetBreed,
    string? PetVaccinationStatus,
    string? PetVaccinationType,
    string? PetVaccinationDose,
    string? PetPrescription,
    string? PetSterilizationStatus = null,
    string? PetMedicalHistory = null,
    string? PetTemperament = null,
    // Optional free-text notes the parent attached to the stay at booking time.
    string? JobNotes = null,
    // The parent's location choice ('ParentLocation'/'ProviderLocation');
    // null for legacy rows.
    string? LocationType = null,
    // Pet-parent address join — null when the parent row is missing.
    string? ParentAddressLine = null,
    string? ParentCity = null,
    string? ParentZipCode = null,
    decimal? ParentLatitude = null,
    decimal? ParentLongitude = null,
    // Snapshot of the offering's per-night rate at booking time (price-lock);
    // null for legacy rows created before snapshotting shipped.
    decimal? PricePerNight = null,
    // Snapshots captured at booking creation (mirror of BookingDetailRow). The
    // detail service PREFERS these over the live provider policy / resolved
    // service-location address, falling back to live only when null (legacy rows).
    int? CancellationPolicyHours = null,
    string? SnapshotAddressLine = null,
    string? SnapshotCity = null,
    string? SnapshotZipCode = null,
    decimal? SnapshotLatitude = null,
    decimal? SnapshotLongitude = null,
    // The payment ledger row ([Booking].[BookingPayments]) for this stay, joined on
    // read. Both are null until the provider records the payment (Status = PAID);
    // from then on PayoutMethod is the 'Cash' / 'Digital' the money actually changed
    // hands as, which the booking row itself never stores.
    string? PayoutMethod = null,
    DateTimeOffset? PaidAtUtc = null);

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
    int? MinimumHoursBeforeCancellation,
    // The resolved "where does the service happen" block, driven by the
    // booking's LocationType. LocationType is null on legacy rows.
    BookingLocationResult Location,
    // The provider's registered business address (Cosmos service doc) — surfaced
    // on providerDetails so the client needn't call GET /providers/{id} for it.
    // Null when the offering can't be resolved.
    string? ProviderAddress = null,
    string? ProviderCity = null,
    string? ProviderZip = null,
    // The provider's photo, from the same Cosmos service doc: the business image
    // for hotels, the freelancer's profile image otherwise. The SQL provider row
    // has no photo column, which is why providerDetails used to report null here.
    string? ProviderPhotoUrl = null);

/// <summary>
/// Request to move a night-stay booking to a new lifecycle status. Same shape
/// and actor rules as <see cref="UpdateBookingStatusCommand"/>; <see cref="ActorId"/>
/// is the caller's ProviderId / PetParentId derived from the authenticated route.
/// </summary>
/// <param name="Location">
/// The acting party's position — required for, and only stored on, the two NO-SHOW
/// transitions. See <see cref="UpdateBookingStatusCommand.Location"/>.
/// </param>
public sealed record UpdateNightStayBookingStatusCommand(
    Guid NightStayBookingId,
    string NewStatus,
    BookingStatusActor Actor,
    Guid ActorId,
    string? Note,
    CapturedLocation? Location = null);

/// <summary>
/// Either party proposes a new check-in / check-out range for a night-stay
/// booking. The start (<see cref="StartBookingCommand"/>) and respond
/// (<see cref="RespondBookingModificationCommand"/>) commands are shared with the
/// single-day flow — only the proposed fields differ, so the request has its own
/// command.
/// </summary>
/// <param name="AcknowledgeTermsChanges">
/// The requester has seen and accepted the provider's current terms — see
/// <see cref="RequestBookingModificationCommand.AcknowledgeTermsChanges"/>. For a
/// stay the drifting set also covers the offering's drop-off / pick-up times.
/// </param>
public sealed record RequestNightStayBookingModificationCommand(
    Guid NightStayBookingId,
    BookingStatusActor Actor,
    Guid ActorId,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    string? Note,
    bool AcknowledgeTermsChanges = false);

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
    DateTimeOffset CreatedAtUtc,
    // The provider's terms as confirmed by the requester, staged because they had
    // drifted from what the stay froze. Null when they hadn't.
    BookingAcknowledgedTerms? AcknowledgedTerms = null);

/// <summary>The requested service id is not a NightStay service of this provider.</summary>
public sealed class BookingNotNightStayServiceException(Guid serviceId, Guid providerId)
    : Exception($"Service '{serviceId}' is not a NightStay service for provider '{providerId}'.");

/// <summary>Check-in / check-out dates are invalid (order or span out of range).</summary>
public sealed class InvalidNightStayDatesException(string message) : Exception(message);

/// <summary>One or more nights in the requested stay have no remaining capacity.</summary>
public sealed class NightStayCapacityExceededException(Guid serviceId, DateOnly checkInDate, DateOnly checkOutDate)
    : Exception($"Service '{serviceId}' has no remaining capacity for one or more nights between {checkInDate:yyyy-MM-dd} and {checkOutDate:yyyy-MM-dd}.");

/// <summary>
/// The pet already has an active stay on this service whose date range overlaps the
/// requested one — a pet can't board in two places at once. Enforced server-side so
/// two devices / a race can't slip a duplicate stay through.
/// </summary>
public sealed class NightStayPetAlreadyBookedException(Guid petId, Guid serviceId, DateOnly checkInDate, DateOnly checkOutDate)
    : Exception($"Pet '{petId}' already has a stay on service '{serviceId}' overlapping {checkInDate:yyyy-MM-dd} to {checkOutDate:yyyy-MM-dd}.");

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

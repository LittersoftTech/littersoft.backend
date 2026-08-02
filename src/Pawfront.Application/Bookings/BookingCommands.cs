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
    Guid? PetId = null,
    // Where the service is delivered: ParentLocation or ProviderLocation
    // (see <see cref="BookingLocationTypes"/>). Required on the parent host,
    // optional on the provider host. The detail read resolves the address live.
    string? LocationType = null);

/// <summary>
/// Canonical values for a booking's location choice — where the service is
/// delivered, picked by the parent at booking time.
/// </summary>
public static class BookingLocationTypes
{
    public const string ParentLocation = "ParentLocation";
    public const string ProviderLocation = "ProviderLocation";

    /// <summary>
    /// Trims and validates an optional location-type value. Returns null for
    /// null/blank input; throws <see cref="UnsupportedBookingLocationTypeException"/>
    /// for anything that isn't one of the two canonical values.
    /// </summary>
    public static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed switch
        {
            ParentLocation => ParentLocation,
            ProviderLocation => ProviderLocation,
            _ => throw new UnsupportedBookingLocationTypeException(trimmed)
        };
    }
}

/// <summary>The supplied location type is not ParentLocation / ProviderLocation.</summary>
public sealed class UnsupportedBookingLocationTypeException(string value)
    : Exception($"Location type '{value}' is not supported. Use 'ParentLocation' or 'ProviderLocation'.");

/// <summary>
/// The resolved "where does the service happen" block on a booking-detail read.
/// For ParentLocation the address comes from the pet parent's profile; for
/// ProviderLocation from the provider's registered business address (Cosmos
/// service doc) + registration coordinates. Address fields are null when the
/// lookup can't be resolved (best-effort).
/// </summary>
public sealed record BookingLocationResult(
    string? LocationType,
    string? AddressLine,
    string? City,
    string? ZipCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>
/// The provider's business address, resolved in the Application layer (Cosmos
/// service doc + registration coordinates) and passed to the create sproc so a
/// ProviderLocation booking can snapshot its "where the service happens" address.
/// Null for ParentLocation bookings (the sproc snapshots the parent's address from
/// SQL instead) and for bookings with no location type.
/// </summary>
public sealed record ProviderAddressSnapshot(
    string? AddressLine,
    string? City,
    string? ZipCode,
    decimal? Latitude,
    decimal? Longitude);

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

/// <summary>
/// One row of the parent's "my bookings" list: the flat booking plus the
/// frozen-at-creation extras the list cards surface — the cancellation-policy
/// snapshot and the selected-location address snapshot (the frozen price already
/// travels on <see cref="BookingResult.PricePerHour"/>). The location block carries
/// ONLY the snapshot (no live fallback on list reads — legacy rows show nulls;
/// the booking-detail read remains the live-fallback authority).
/// </summary>
public sealed record BookingListItemResult(
    BookingResult Booking,
    int? CancellationPolicyHours,
    BookingLocationResult Location);

/// <summary>Lightweight pair used by the slot service to subtract overlaps.</summary>
public sealed record BookingWindow(TimeOnly StartTime, TimeOnly EndTime);

/// <summary>
/// One occupied window on a service's day, with the identity the daily agenda
/// needs (<c>Booking.GetAgendaForDate</c>). Same rows the slot service counts
/// as overlaps, plus who booked it and what state the job is in.
/// <para>
/// <see cref="PetParentId"/> is what lets the agenda tell the caller's own jobs
/// apart from everyone else's — the API reveals <see cref="JobNumber"/> and
/// <see cref="Status"/> only on the caller's own rows. It is null for Custom
/// walk-ins (provider-entered private jobs), which therefore always mask.
/// </para>
/// A night stay occupies its bucket for the whole night, so it arrives as a
/// full-day window (00:00–23:59:59) with <see cref="BookingType"/> =
/// <c>NightStay</c>.
/// </summary>
public sealed record AgendaBookingRow(
    string BookingType,
    Guid BookingId,
    int JobNumber,
    Guid? PetParentId,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Status);

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
    string? PetPhotoUrl,
    // Provider join — present for both App and Custom bookings.
    string? ProviderFirstName,
    string? ProviderLastName,
    string? ProviderGender,
    string? ProviderMobileCountryCode,
    string? ProviderMobileNumber,
    // Pet medical extras (App bookings) — null for Custom rows.
    string? PetBreed,
    string? PetVaccinationStatus,
    string? PetVaccinationType,
    string? PetVaccinationDose,
    string? PetPrescription,
    string? PetSterilizationStatus = null,
    string? PetMedicalHistory = null,
    string? PetTemperament = null,
    // The parent's location choice ('ParentLocation'/'ProviderLocation');
    // null for Custom walk-ins and legacy rows.
    string? LocationType = null,
    // Pet-parent address join (App bookings) — null for Custom rows.
    string? ParentAddressLine = null,
    string? ParentCity = null,
    string? ParentZipCode = null,
    decimal? ParentLatitude = null,
    decimal? ParentLongitude = null,
    // Vet prescription snapshot (Booking.BookingPrescriptions) — present only
    // when a vet has recorded one for this booking. HasPrescription is the
    // presence flag; the rest are null/empty otherwise. NextConsultationDate is
    // the pet's rolling Vet follow-up (Parent.PetNextConsultations), joined here
    // rather than stored on the prescription row.
    bool HasPrescription = false,
    string? PrescriptionText = null,
    bool? IsPetVaccinated = null,
    IReadOnlyList<string>? PrescriptionVaccinations = null,
    DateOnly? NextConsultationDate = null,
    // Snapshots captured at booking creation (price-lock siblings). The detail
    // service PREFERS these over the live provider policy / resolved service-location
    // address, falling back to live only when they're null (legacy rows).
    // CancellationPolicyHours null legitimately means "no cancellation restriction".
    int? CancellationPolicyHours = null,
    string? SnapshotAddressLine = null,
    string? SnapshotCity = null,
    string? SnapshotZipCode = null,
    decimal? SnapshotLatitude = null,
    decimal? SnapshotLongitude = null);

/// <summary>
/// Fully resolved booking-detail view: the raw <see cref="Row"/> plus the friendly
/// Job ID and the live-computed payment figures (unit price, total, Pawfront fee,
/// and the fee percentage applied). Pricing fields are null when the offering can't
/// be resolved (e.g. the service was deactivated). Private (Custom walk-in) jobs
/// carry zero fee + fee percentage — Pawfront takes no commission on them. Mapped
/// to the four-section response in the endpoint layer.
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
    int? MinimumHoursBeforeCancellation,
    // The resolved "where does the service happen" block, driven by the
    // booking's LocationType. LocationType is null on legacy/Custom rows.
    BookingLocationResult Location,
    // The provider's registered business address (Cosmos service doc) — surfaced
    // on providerDetails so the client needn't call GET /providers/{id} for it.
    // Null when the offering can't be resolved.
    string? ProviderAddress = null,
    string? ProviderCity = null,
    string? ProviderZip = null);

/// <summary>Which party is driving a booking status change.</summary>
public enum BookingStatusActor
{
    Provider,
    Parent
}

/// <summary>
/// Canonical values for how the parent paid the provider — the same two-value
/// vocabulary as the provider's payout methods.
/// </summary>
public static class BookingPaymentMethods
{
    public const string Cash = "Cash";
    public const string Digital = "Digital";

    /// <summary>
    /// Trims and validates a payment-method value. Throws
    /// <see cref="UnsupportedBookingPaymentMethodException"/> for anything that
    /// isn't Cash or Digital.
    /// </summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed switch
        {
            Cash => Cash,
            Digital => Digital,
            _ => throw new UnsupportedBookingPaymentMethodException(value ?? string.Empty)
        };
    }
}

/// <summary>The supplied payment method is not Cash / Digital.</summary>
public sealed class UnsupportedBookingPaymentMethodException(string value)
    : Exception($"Payment method '{value}' is not supported. Use 'Cash' or 'Digital'.");

/// <summary>
/// The provider records that the parent has paid for a booking (COMPLETED → PAID).
/// The amount is computed server-side from the booking's price-locked snapshot —
/// only the payment method comes from the caller.
/// </summary>
public sealed record MarkBookingPaidCommand(
    Guid BookingId,
    Guid ProviderId,
    string PaymentMethod);

/// <summary>
/// A recorded booking payment (one per paid booking). <see cref="Amount"/> is the
/// price-locked total the parent paid; <see cref="PawfrontFee"/> is the platform
/// commission on it (provider net = Amount − PawfrontFee).
/// </summary>
public sealed record BookingPaymentResult(
    Guid BookingId,
    Guid ProviderId,
    Guid PetParentId,
    decimal Amount,
    decimal PawfrontFee,
    string PaymentMethod,
    DateTimeOffset PaidAtUtc);

/// <summary>The booking can't be marked paid from its current status — payment is only allowed from COMPLETED.</summary>
public sealed class BookingNotPayableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' must be completed before it can be marked paid.");

/// <summary>The booking has already been marked paid.</summary>
public sealed class BookingAlreadyPaidException(Guid bookingId)
    : Exception($"Booking '{bookingId}' has already been marked paid.");

/// <summary>A payment was attempted on a Custom (walk-in) booking — only App bookings can be paid.</summary>
public sealed class BookingPaymentNotAppException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is a private walk-in job and cannot be marked paid.");

/// <summary>The booking can't be priced (no snapshot and the offering is gone), so no payment amount can be recorded.</summary>
public sealed class BookingNotPriceableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' cannot be priced, so a payment amount cannot be recorded.");

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

/// <summary>
/// The pet already has an active booking on this service overlapping the requested
/// slot — a pet can't be double-booked for the same time window. Enforced
/// server-side so two devices / a race can't slip a duplicate through.
/// </summary>
public sealed class PetAlreadyBookedException(Guid petId, Guid serviceId, DateOnly date, TimeOnly startTime, TimeOnly endTime)
    : Exception($"Pet '{petId}' already has a booking on service '{serviceId}' overlapping {date} {startTime}-{endTime}.");

public sealed class BookingCancellationForbiddenException(Guid bookingId)
    : Exception($"Only the original booker can cancel booking '{bookingId}'.");

public sealed class BookingAlreadyCancelledException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is already cancelled.");

public sealed class InvalidBookingTimeException(string message) : Exception(message);

/// <summary>
/// The requested service starts inside the <see cref="BookingLeadTime.Minimum"/>
/// window — a booking has to be made at least that far ahead (see
/// <see cref="BookingLeadTime"/>). Raised by the App-booking create paths on both
/// hosts, single-day and night-stay. A Custom walk-in is exempt: the provider is
/// recording a job that is happening now, not booking one in advance.
/// </summary>
public sealed class BookingLeadTimeTooShortException(DateTimeOffset earliestStartUtc)
    : Exception(
        $"A booking must start at least {BookingLeadTime.Minimum.TotalHours:0.#} hours from now. "
        + $"The earliest available start is {earliestStartUtc:yyyy-MM-dd HH:mm} UTC.")
{
    /// <summary>The earliest service start the caller may request right now (UTC).</summary>
    public DateTimeOffset EarliestStartUtc { get; } = earliestStartUtc;
}

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

/// <summary>
/// A no-show was reported before the counterparty is actually late — the
/// 30-minute grace window after the booking's scheduled start hasn't elapsed.
/// </summary>
public sealed class BookingNoShowTooEarlyException(Guid bookingId)
    : Exception($"A no-show on booking '{bookingId}' can only be reported 30 minutes after its scheduled start.");

/// <summary>
/// The booking was never accepted in time, so it is expired — no further status
/// change (including accept) is possible. Two triggers produce it, both meaning
/// the provider ran out of time: sitting in CREATED for 24+ hours (BR-17), or
/// still sitting in CREATED with under <see cref="BookingLeadTime.Minimum"/> to
/// the service (BR-53). Both surface as 409 <c>BookingExpired</c>; only the
/// message differs.
/// <para>
/// The stored status may still read CREATED when this is thrown: the sprocs
/// reject the transition, and the scheduled external job is the only writer of
/// EXPIRED.
/// </para>
/// </summary>
public sealed class BookingExpiredException(Guid bookingId, string reason)
    : Exception($"Booking '{bookingId}' has expired {reason} and can no longer change.")
{
    /// <summary>BR-17 — 24+ hours in CREATED without the provider accepting.</summary>
    public static BookingExpiredException NeverAccepted(Guid bookingId)
        => new(bookingId, "after 24 hours awaiting provider acceptance");

    /// <summary>BR-53 — still in CREATED with the service now too close to start.</summary>
    public static BookingExpiredException ServiceTooClose(Guid bookingId)
        => new(bookingId,
            $"because it was never accepted and the service now starts in under {BookingLeadTime.Minimum.TotalHours:0.#} hours");
}

// --- Job lifecycle: start-OTP, evidence, modifications ----------------------

/// <summary>
/// The verification OTP issued for a booking when the provider taps "Start Job"
/// (→ START_JOB). The plaintext <see cref="OtpCode"/> is surfaced to the parent
/// (who reads it to the provider); the provider posts it back to move the job to
/// IN_PROGRESS. Completion needs no OTP. Shared by single-day and night-stay
/// bookings.
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

/// <summary>
/// Provider taps "Start Job": moves a confirmed-equivalent booking to START_JOB and
/// issues the parent-facing start-OTP. Allowed only while the provider is inside
/// their own weekly working hours. The provider then enters the code the parent
/// shows to move the job to IN_PROGRESS.
/// </summary>
public sealed record StartBookingCommand(Guid BookingId, Guid ProviderId);

/// <summary>
/// Either party proposes a new date/time for a single-day booking (editing is
/// limited to the schedule). The proposed window is validated (working hours,
/// closures, duration) before the proposal is staged; on accept capacity is
/// re-checked race-safely.
/// </summary>
/// <param name="AcknowledgeTermsChanges">
/// The requester has seen and accepted the provider's terms as they stand now.
/// Required only when those terms have drifted from what the booking froze at
/// creation (price, cancellation policy, the selected-location address) — an
/// un-acknowledged request against a drifted booking is rejected with
/// <see cref="BookingTermsChangedException"/>. When set, the current terms are
/// staged with the proposal and applied if the counterparty accepts.
/// </param>
public sealed record RequestBookingModificationCommand(
    Guid BookingId,
    BookingStatusActor Actor,
    Guid ActorId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Note,
    bool AcknowledgeTermsChanges = false);

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
/// <param name="AcknowledgedTerms">
/// The provider's terms as confirmed by the requester, staged with the proposal
/// because they had drifted from what the booking froze. Null when they hadn't —
/// the ordinary case. Accepting the proposal re-freezes exactly these onto the
/// booking; declining discards them.
/// </param>
public sealed record BookingModificationResult(
    Guid BookingModificationId,
    Guid BookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedBookingDate,
    TimeOnly ProposedStartTime,
    TimeOnly ProposedEndTime,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    BookingAcknowledgedTerms? AcknowledgedTerms = null);

/// <summary>
/// The provider completes the job (IN_PROGRESS → COMPLETED; no OTP), optionally
/// proposing the pet's next consultation date. The consultation type is derived
/// from the booking's service category (PetGroomer → Groomer, Vet → Vet,
/// PetTrainer → Trainer), never from the client. A <see cref="Prescription"/> may
/// also be recorded here (Vet bookings only) — the same data the dedicated
/// prescription endpoint writes.
/// </summary>
public sealed record CompleteBookingCommand(
    Guid BookingId,
    Guid ProviderId,
    DateOnly? NextConsultationDate,
    PrescriptionInput? Prescription = null);

/// <summary>
/// The vet's per-visit prescription payload — free-text notes, the vaccinated
/// flag, and the list of vaccines recorded. Written on job completion (via
/// <see cref="CompleteBookingCommand"/>) or the dedicated upsert endpoint.
/// </summary>
public sealed record PrescriptionInput(
    string? PrescriptionText,
    bool IsPetVaccinated,
    IReadOnlyList<string> Vaccinations);

/// <summary>
/// Records (upserts) the vet's prescription for a booking outside the completion
/// flow. Allowed only for the booking's provider, on a Vet service, once the job
/// has started or completed.
/// </summary>
public sealed record UpsertBookingPrescriptionCommand(
    Guid BookingId,
    Guid ProviderId,
    string? PrescriptionText,
    bool IsPetVaccinated,
    IReadOnlyList<string> Vaccinations);

/// <summary>
/// The saved Vet prescription for a booking. <see cref="NextConsultationDate"/>
/// is the pet's rolling Vet follow-up (Parent.PetNextConsultations), not stored on
/// the prescription row — null when none has been set.
/// </summary>
public sealed record BookingPrescriptionResult(
    Guid BookingId,
    string? PrescriptionText,
    bool IsPetVaccinated,
    IReadOnlyList<string> Vaccinations,
    DateOnly? NextConsultationDate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>A prescription write was attempted by someone who isn't the booking's provider.</summary>
public sealed class BookingPrescriptionForbiddenException(Guid bookingId)
    : Exception($"You are not the provider on booking '{bookingId}' and cannot record a prescription.");

/// <summary>A prescription was attempted on a non-Vet booking.</summary>
public sealed class BookingPrescriptionNotVetException(Guid bookingId)
    : Exception($"A prescription can only be recorded on a Vet booking; '{bookingId}' is not one.");

/// <summary>A prescription was attempted before the job started (or after a non-startable state).</summary>
public sealed class BookingPrescriptionInvalidStateException(Guid bookingId)
    : Exception($"A prescription can only be recorded on booking '{bookingId}' once the job has started or completed.");

/// <summary>A next-consultation date was supplied on a category that has no consultation concept (PetSitter / PetAdoptionAndSale).</summary>
public sealed class NextConsultationNotSupportedException(string serviceCategory)
    : Exception($"A next-consultation date cannot be set for a '{serviceCategory}' booking — only Groomer, Vet, and Trainer bookings support one.");

/// <summary>A next-consultation date was supplied on a booking with no linked pet (Custom walk-in / legacy row).</summary>
public sealed class NextConsultationRequiresPetException(Guid bookingId)
    : Exception($"Booking '{bookingId}' has no linked pet to store a next-consultation date on.");

/// <summary>The supplied next-consultation date is in the past.</summary>
public sealed class InvalidNextConsultationDateException(DateOnly date)
    : Exception($"The next-consultation date '{date:yyyy-MM-dd}' must not be in the past.");

/// <summary>The booking is not in a state the job can be started (or start-code verified) from.</summary>
public sealed class BookingNotStartableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is not in a state the job can be started from.");

/// <summary>
/// The provider tapped "Start Job" outside their own weekly working hours — a job
/// can only be started while the provider is open (their saved availability for
/// today, UTC). The time of day the booking itself is scheduled for does not gate
/// the action; only the working-hours window and the service DATE do (see
/// <see cref="BookingStartNotOnServiceDateException"/>).
/// </summary>
public sealed class BookingStartOutsideWorkingHoursException(Guid bookingId)
    : Exception($"The job on booking '{bookingId}' can only be started during your working hours.");

/// <summary>
/// The provider tapped "Start Job" on a day other than the one the booking is
/// scheduled for (single-day: BookingDate; night-stay: CheckInDate, the drop-off
/// day) — both compared against today in UTC. The time of day within the booked
/// window is not checked, so a provider who runs early or late can still start.
/// </summary>
public sealed class BookingStartNotOnServiceDateException(Guid bookingId)
    : Exception($"The job on booking '{bookingId}' can only be started on the day it is scheduled for.");

/// <summary>
/// The job is already underway (IN_PROGRESS), so it can no longer be
/// cancelled — it must run through to completion (or a no-show reported earlier).
/// </summary>
public sealed class BookingJobInProgressException(Guid bookingId)
    : Exception($"The job on booking '{bookingId}' is already in progress and can no longer be cancelled.");

/// <summary>The provider-entered start-OTP is missing or incorrect.</summary>
public sealed class InvalidStartOtpException(Guid bookingId)
    : Exception($"The start code for booking '{bookingId}' is missing or incorrect.");

/// <summary>The start-OTP has expired; the parent must refresh the booking.</summary>
public sealed class StartOtpExpiredException(Guid bookingId)
    : Exception($"The start code for booking '{bookingId}' has expired.");

/// <summary>
/// The provider entered the wrong start-OTP too many times (the 6th failed
/// attempt), so the job has been cancelled with the terminal
/// OTP_MAX_ATTEMPTS_EXCEEDED status — no further status change is possible.
/// </summary>
public sealed class OtpAttemptsExceededException(Guid bookingId)
    : Exception($"The verification code for booking '{bookingId}' was entered incorrectly too many times; the job has been cancelled.");

/// <summary>
/// The job can't be completed from the booking's current status — completion is
/// only allowed once the job is IN_PROGRESS (the start-OTP was verified).
/// </summary>
public sealed class BookingNotCompletableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is not in a state the job can be completed from.");

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

/// <summary>
/// Either party tried to modify a booking less than 2 hours before the service
/// starts (BookingDate + StartTime; CheckInDate + DropOffTime for a stay). Past
/// that point the schedule needs to be settled so the job can start — the app
/// hides "Modify Job" from the same cutoff. Widened 2026-08-02 to gate the
/// provider identically; previously only the parent was gated.
/// </summary>
public sealed class BookingModificationWindowClosedException(Guid bookingId)
    : Exception($"Booking '{bookingId}' can no longer be modified within 2 hours of the service start time.");

/// <summary>
/// The proposal — from either party (widened 2026-08-02; previously
/// parent-only) — was still unanswered 2 hours before the service starts, so it
/// can no longer be answered. Raised when the counterparty responds after that
/// cutoff. The sproc REJECTS ONLY — the revert to CONFIRMED and the discard of
/// the staging row are the scheduled external job's, so the booking may still be
/// sitting in whichever MODIFICATION_REQUEST_BY_* status it was in when the
/// caller sees this.
/// </summary>
public sealed class BookingModificationExpiredException(Guid bookingId)
    : Exception($"The modification request for booking '{bookingId}' expired before it was answered; the booking has reverted to CONFIRMED.");

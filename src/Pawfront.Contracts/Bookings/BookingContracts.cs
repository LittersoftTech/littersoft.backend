using Pawfront.Contracts.Reviews;

namespace Pawfront.Contracts.Bookings;

public sealed record CreateBookingRequest(
    Guid PetParentId,
    Guid ServiceId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? ServiceItemCode,
    // Optional free-text notes for the job (access instructions, the pet's
    // quirks, etc.). Captured at create time and surfaced on the booking detail.
    string? JobNotes,
    // Where the service is delivered: "ParentLocation" or "ProviderLocation".
    // Optional on the provider host; the booking detail resolves the matching
    // address live.
    string? LocationType = null);

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
    string? ServiceItemCode,
    // Optional free-text notes for the job (access instructions, the pet's
    // quirks, etc.). Captured at create time and surfaced on the booking detail.
    string? JobNotes,
    // Where the service is delivered: "ParentLocation" (the provider comes to
    // the parent's address) or "ProviderLocation" (the parent goes to the
    // provider's place). Required. The booking detail resolves the matching
    // address live.
    string? LocationType = null);

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

/// <summary>
/// Provider edits a walk-in they recorded earlier. Same fields as
/// <see cref="CreateCustomBookingRequest"/> — a FULL REPLACE, so send every field,
/// including the ones you are not changing. A partial body would make "cleared the
/// notes" and "left the notes alone" indistinguishable.
/// </summary>
/// <remarks>
/// Walk-ins only. An App booking is a two-party agreement and changes go through
/// the modification flow instead, where the parent accepts or declines.
/// <para>
/// <c>pricePerHour</c>, the customer/pet details, the location text and the notes
/// stay editable through COMPLETED — correcting the money after the job is the main
/// reason this exists, and a walk-in can never be marked paid, so no ledger row
/// contradicts it. <c>serviceId</c>, <c>bookingDate</c>, <c>startTime</c> and
/// <c>endTime</c> lock once the job starts; changing one after that is refused
/// rather than silently dropped.
/// </para>
/// </remarks>
public sealed record UpdateCustomBookingRequest(
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
/// Body for the batch cancellation endpoints
/// (<c>POST /providers/{providerId}/bookings/bulk-cancel</c> and
/// <c>POST /pet-parents/{petParentId}/bookings/bulk-cancel</c>). The acting party
/// and its id come from the authenticated route, not the body — so a batch can
/// only ever cancel the caller's own bookings.
/// </summary>
/// <param name="Bookings">
/// One entry per booking, each naming its own kind. At least one, at most 50.
/// </param>
/// <param name="Note">
/// Optional free text recorded on every cancelled booking's audit row. One note
/// covers the whole batch.
/// </param>
public sealed record BulkCancelBookingsRequest(
    IReadOnlyList<BulkCancelBookingItemRequest>? Bookings,
    string? Note = null);

/// <summary>
/// One booking in a batch. <paramref name="BookingType"/> is required and is
/// either "SingleDay" (<c>Booking.Bookings</c>) or "NightStay" (multi-night
/// boarding) — the two kinds live in separate tables and share no id space, so
/// the id alone cannot say which one it is.
/// </summary>
public sealed record BulkCancelBookingItemRequest(Guid BookingId, string? BookingType);

/// <summary>
/// The batch's outcome. Always returned with <b>200</b> when the batch itself was
/// well-formed, even if every booking in it was refused — the per-booking verdict
/// is in <paramref name="Results"/>, not in the HTTP status. A 400 means the batch
/// was unusable (no bookings, or more than 50).
/// </summary>
/// <param name="RequestedCount">
/// Distinct bookings processed, after duplicate ids in the request are collapsed.
/// Always equals <paramref name="Results"/>.length, and
/// <paramref name="CancelledCount"/> + <paramref name="FailedCount"/>.
/// </param>
public sealed record BulkCancelBookingsResponse(
    int RequestedCount,
    int CancelledCount,
    int FailedCount,
    IReadOnlyList<BulkCancelBookingItemResponse> Results);

/// <summary>
/// What happened to one booking. A cancelled booking carries its new
/// <paramref name="Status"/> and <paramref name="CancelledAtUtc"/>; a refused one
/// carries <paramref name="ErrorCode"/> and <paramref name="Message"/> instead.
/// The error codes are the same ones the single-booking cancel endpoints return
/// (BookingNotFound / NightStayBookingNotFound / Forbidden / BookingStatusTerminal
/// / BookingStatusUnchanged / BookingInProgress / BookingExpired /
/// UnsupportedBookingType / InvalidRequest).
/// </summary>
public sealed record BulkCancelBookingItemResponse(
    Guid BookingId,
    string BookingType,
    bool Cancelled,
    string? Status,
    DateTimeOffset? CancelledAtUtc,
    string? ErrorCode,
    string? Message);

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
/// <summary>
/// A geolocation fix captured on the device at the moment of the action. Sent on
/// every booking action that has to be evidenced — arrival, job start, no-show, the
/// parent showing their start code, cash received / not received, and evidence
/// capture — and REQUIRED on all of them: the action is refused with 400
/// <c>InvalidRequest</c> when it is missing or out of range.
/// <para>
/// Each app sends its OWN position. The server never derives one party's location
/// from the other's, so for the moments the provider drives (no-show, cash, photo
/// evidence) the parent app posts its own fix separately to
/// <c>POST .../bookings/{bookingId}/location</c>.
/// </para>
/// </summary>
/// <param name="AccuracyMetres">
/// The device's reported horizontal accuracy. Optional, but worth sending — it is
/// what separates "was at the address" from "was somewhere in the city".
/// </param>
/// <param name="CapturedAtUtc">
/// When the device took the reading. Optional and untrusted (it is the handset's
/// clock); the server stamps its own receipt time regardless.
/// </param>
public sealed record CapturedLocationRequest(
    decimal? Latitude,
    decimal? Longitude,
    decimal? AccuracyMetres = null,
    DateTimeOffset? CapturedAtUtc = null);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/status</c> — the LEGACY generic status
/// shim. <see cref="Location"/> is required when <see cref="Status"/> is one of the
/// two no-show values and ignored otherwise; without it the shim would be a way to
/// record a no-show with no position, which the dedicated route refuses.
/// </summary>
public sealed record UpdateBookingStatusRequest(
    string Status,
    string? Note,
    CapturedLocationRequest? Location = null);

/// <summary>
/// Body for the two <c>POST .../bookings/{bookingId}/no-show</c> routes and for
/// <c>POST .../bookings/{bookingId}/cash-not-received</c> — actions whose only
/// payload is where the caller was when they took it.
/// </summary>
public sealed record BookingLocationOnlyRequest(CapturedLocationRequest? Location);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/start-job</c> (provider). The provider
/// reaches this by answering the arrival question — "have you arrived at the
/// customer's location?" for a ParentLocation booking, "has the customer arrived?"
/// for a ProviderLocation one. Which question was asked is derived server-side from
/// the booking, so it is not part of the body.
/// </summary>
public sealed record StartJobRequest(CapturedLocationRequest? Location);

/// <summary>
/// Body for <c>POST /pet-parents/{petParentId}/bookings/{bookingId}/start-otp</c> —
/// the parent asking to see their start code.
/// <para>
/// This route exists BECAUSE the fix is required: the code used to be handed out as
/// a side effect of the booking-detail GET, and a GET cannot carry a body, so
/// leaving it there would have left a way to obtain the code while supplying no
/// position at all. The detail read no longer returns <c>startOtp</c>.
/// </para>
/// </summary>
public sealed record IssueStartOtpRequest(CapturedLocationRequest? Location);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/location</c> — a party recording their
/// OWN position for a moment the COUNTERPARTY drove. <see cref="Trigger"/> is one of
/// <c>NoShowMarked</c>, <c>CashReceived</c>, <c>CashNotReceived</c>,
/// <c>EvidenceCaptured</c> (case-insensitive). The three triggers tied to a
/// transition the caller performs themselves cannot be posted here — they are
/// written by the transition itself.
/// </summary>
public sealed record RecordBookingLocationRequest(
    string Trigger,
    CapturedLocationRequest? Location);

/// <summary>One recorded geolocation fix, as echoed back after it is stored.</summary>
public sealed record BookingLocationEventResponse(
    Guid BookingLocationEventId,
    Guid BookingId,
    string Trigger,
    string CapturedByType,
    Guid CapturedById,
    decimal Latitude,
    decimal Longitude,
    decimal? AccuracyMetres,
    DateTimeOffset? DeviceCapturedAtUtc,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/complete</c> (provider). Completion
/// itself needs no OTP (the parent's code gates only <c>.../start-job/verify</c>),
/// so the whole body is OPTIONAL — send it only to attach the extras below.
/// <see cref="NextConsultationDate"/> lets the provider propose the pet's next
/// visit while completing the job — stored on the pet (one entry per provider type;
/// the type is derived server-side from the booking's service category:
/// PetGroomer → Groomer, Vet → Vet, PetTrainer → Trainer). Omit the field to
/// complete without one. Only valid for those three categories AND when the
/// booking has a linked pet (App bookings).
/// <see cref="Prescription"/> lets a vet record the visit's prescription while
/// completing the job (Vet bookings only) — the same payload the dedicated
/// <c>POST .../bookings/{id}/prescription</c> endpoint accepts. Omit for non-vet jobs.
/// </summary>
public sealed record CompleteBookingRequest(
    DateOnly? NextConsultationDate = null,
    PrescriptionRequest? Prescription = null);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/paid</c> (provider) — records that
/// the parent has paid the provider (COMPLETED → PAID). The amount is computed
/// server-side from the booking's price-locked total, so the only field is how
/// the parent paid. <see cref="PaymentMethod"/> is 'Cash' or 'Digital'.
/// </summary>
/// <param name="Location">
/// Where the provider was when the cash changed hands. Required. The parent's own
/// fix for the same moment is posted separately with the <c>CashReceived</c> trigger.
/// </param>
public sealed record MarkBookingPaidRequest(
    string PaymentMethod,
    CapturedLocationRequest? Location = null);

/// <summary>
/// The vet's per-visit prescription payload. Body for
/// <c>POST /providers/{providerId}/bookings/{bookingId}/prescription</c> and the
/// optional <c>prescription</c> block on the complete-booking body. The
/// next-consultation date is NOT part of this payload — it's set via the
/// complete-booking <see cref="CompleteBookingRequest.NextConsultationDate"/> and
/// surfaced (joined) on the read.
/// </summary>
public sealed record PrescriptionRequest(
    string? PrescriptionText,
    bool IsPetVaccinated,
    IReadOnlyList<string>? Vaccinations);

/// <summary>
/// The Vet prescription block on a booking-detail read — populated only once a vet
/// has recorded one (null otherwise). <see cref="NextConsultationDate"/> is the
/// pet's rolling Vet follow-up (from the complete-booking flow), joined here; null
/// when none has been set. Also returned by the dedicated prescription upsert.
/// </summary>
public sealed record PrescriptionDetailsSection(
    string? PrescriptionText,
    bool IsPetVaccinated,
    IReadOnlyList<string> Vaccinations,
    DateOnly? NextConsultationDate);

/// <summary>
/// The parent-facing start-OTP block, surfaced on the parent's booking-details
/// read while the booking is START_JOB. The parent reads the code to the
/// provider, who enters it to move the job to IN_PROGRESS.
/// </summary>
public sealed record StartOtpResponse(string Code, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Body for <c>POST .../bookings/{bookingId}/start-job/verify</c> (provider) — the
/// start-code the parent showed. Entering the correct code moves the booking
/// START_JOB → IN_PROGRESS (6 wrong attempts cancel the job).
/// </summary>
/// <param name="Location">
/// Where the provider is as they tap "Proceed to start". Required on the request,
/// but stored only when the job actually starts — a wrong code is not a job start.
/// </param>
public sealed record VerifyStartOtpRequest(
    string OtpCode,
    CapturedLocationRequest? Location = null);

/// <summary>
/// Body for a single-day modification request
/// (<c>POST .../bookings/{bookingId}/modifications</c>). Editing is limited to the
/// schedule — date + time window only.
/// </summary>
/// <param name="AcknowledgeTermsChanges">
/// Set when the user has confirmed the provider's terms as they stand now. Only
/// needed if those terms have drifted from what the booking froze at creation —
/// read the drift from <c>GET .../bookings/{bookingId}/terms-changes</c>, show the
/// confirmation sheet, and resubmit with this set. Submitting without it against a
/// drifted booking returns <c>409 BookingTermsChanged</c>. Accepting the proposal
/// then re-freezes the confirmed terms onto the booking.
/// </param>
public sealed record RequestBookingModificationRequest(
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Note,
    bool AcknowledgeTermsChanges = false);

/// <summary>
/// The provider's terms that have changed since a booking was created, so the app
/// can show the "these changed — still want to reschedule?" sheet before the user
/// commits. <see cref="HasChanges"/> false (and an empty
/// <see cref="Changes"/> list) is the ordinary case — no sheet.
/// </summary>
public sealed record BookingTermsChangesResponse(
    Guid BookingId,
    bool HasChanges,
    IReadOnlyList<BookingTermsChangeResponse> Changes);

/// <summary>
/// One changed term. <see cref="BookedValue"/> / <see cref="CurrentValue"/> are
/// ready-to-display strings, since the fields range over money, hours, clock
/// times, and a postal address.
/// </summary>
/// <param name="Field">
/// Stable key: <c>Price</c>, <c>CancellationPolicy</c>, <c>DropOffTime</c>,
/// <c>PickUpTime</c>, <c>Location</c>, <c>Duration</c>, <c>MinimumDuration</c>,
/// <c>MinimumNights</c>.
/// </param>
/// <param name="ChangeType">
/// <c>ValueChanged</c> — the new value is adopted when the modification is
/// accepted. <c>RuleViolation</c> — the booked window no longer satisfies a
/// changed duration/nights rule, so the user has to pick a conforming one.
/// </param>
public sealed record BookingTermsChangeResponse(
    string Field,
    string ChangeType,
    string? BookedValue,
    string? CurrentValue,
    string Message);

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
/// <param name="AcknowledgedTerms">
/// The provider's terms the requester confirmed when proposing, staged because
/// they had drifted from what the booking froze. Null in the ordinary case.
/// Non-null tells the responder that accepting also re-prices / re-rules the
/// booking, not just its schedule.
/// </param>
public sealed record BookingModificationResponse(
    Guid BookingModificationId,
    Guid BookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedBookingDate,
    TimeOnly ProposedStartTime,
    TimeOnly ProposedEndTime,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    AcknowledgedTermsResponse? AcknowledgedTerms = null);

/// <summary>
/// The terms a modification proposal will apply to the booking if accepted.
/// <see cref="PricePerUnit"/> is per hour on a single-day booking, per night on a
/// stay; drop-off / pick-up are night-stay only.
/// </summary>
public sealed record AcknowledgedTermsResponse(
    decimal? PricePerUnit,
    int? MinimumHoursBeforeCancellation,
    TimeOnly? DropOffTime,
    TimeOnly? PickUpTime,
    string? AddressLine,
    string? City,
    string? ZipCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>One job-completion evidence photo.</summary>
public sealed record BookingEvidenceResponse(
    Guid BookingEvidenceId,
    Guid BookingId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Single booking-detail read, grouped into sections — Booking, Parent, Pet,
/// Provider, and Payment — plus the start-OTP (populated only when the booking is in a
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
    ProviderDetailsSection ProviderDetails,
    PaymentDetailsSection PaymentDetails,
    // The provider's advertised cancellation policy for this booking's service,
    // surfaced as its own section.
    CancellationPolicyDetailsSection CancellationPolicy,
    // Where the service is delivered, resolved from the booking's LocationType:
    // the parent's address for ParentLocation, the provider's for
    // ProviderLocation. Fields are null when the type is unset (legacy/Custom
    // rows) or the address can't be resolved.
    BookingLocationDetailsSection Location,
    StartOtpResponse? StartOtp,
    BookingModificationResponse? PendingModification,
    // The Vet prescription recorded for this booking — null until a vet records
    // one (Vet bookings only). Drives the parent app's "View Prescription" screen.
    PrescriptionDetailsSection? Prescription,
    // The caller's OWN review of this booking, plus whether the booking is in a
    // reviewable state at all. Each host reports its own side: the parent host the
    // parent's review of the provider, the provider host the provider's rating of
    // the parent. A provider's rating of a parent is never returned on the parent
    // host. Drives the "Rate your experience" prompt and its edit state.
    BookingReviewDetailsSection? Review = null,
    // The caller's own open support ticket on this booking — see the trio's full
    // note on BookingResponse. Mine only, open only; drives whether the screen
    // offers "Report Incident" or a link to the ticket already raised.
    bool IsTicketRaisedByMe = false,
    Guid? TicketId = null,
    string? TicketRef = null,
    // Whether the OTHER party on this booking is blocked, in either direction.
    // A booking outlives the block that severs the pair -- the history is real
    // and both sides keep it -- so the flag exists to let the app label an
    // existing booking rather than leave the user wondering why they can no
    // longer message or rebook.
    bool IsBlocked = false,
    // True only when the CALLER placed it: the one case where an Unblock action
    // belongs. A block placed against them reads true/false and should show a
    // neutral state -- saying more would confirm the other party acted.
    bool BlockedByMe = false);

/// <summary>
/// The resolved "where does the service happen" block on a booking-detail read.
/// <see cref="LocationType"/> is <c>ParentLocation</c> or <c>ProviderLocation</c>
/// (the parent's choice at booking time; null on legacy/Custom rows). The
/// address fields carry the matching party's address: the pet parent's profile
/// address for ParentLocation, the provider's registered business address for
/// ProviderLocation. Null when unresolvable (e.g. deregistered provider).
/// </summary>
public sealed record BookingLocationDetailsSection(
    string? LocationType,
    string? AddressLine,
    string? City,
    string? ZipCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>
/// The provider's advertised cancellation policy for the booked service.
/// <see cref="MinimumHoursBeforeCancellation"/> is null | 24 | 48 | 72 | 96 — the
/// minimum notice (in hours) the provider requires before a cancellation; null
/// when the provider hasn't set one.
/// </summary>
public sealed record CancellationPolicyDetailsSection(
    int? MinimumHoursBeforeCancellation);

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
    // Where the provider delivers the service. For Custom walk-ins this is the
    // booking's own MyLocation/CustomerLocation; for App bookings it's the
    // provider offering's service-location setting (resolved live). Null when the
    // offering can't be resolved.
    string? ServiceLocation,
    // Free-text street address — Custom 'CustomerLocation' walk-ins only.
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
    string? CustomerPhotoUrl,
    // The parent's profile address — App bookings only (null for Custom
    // walk-ins, which have no linked pet-parent record).
    string? AddressLine = null,
    string? City = null,
    string? ZipCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null);

/// <summary>The provider (service-side) facts, joined from the provider's profile —
/// the counterpart of <see cref="ParentDetailsSection"/> so the parent app can show
/// who delivers the service. <see cref="ProviderPhotoUrl"/> comes from the Cosmos
/// offering doc (the business photo for shops/hotels/clinics, the freelancer's own
/// profile image) rather than the SQL profile row, which has no photo column — the
/// same image the discovery and search cards show. Null when the offering can't be
/// resolved or the provider never uploaded one.</summary>
public sealed record ProviderDetailsSection(
    Guid ProviderId,
    string? ProviderName,
    string? ProviderMobileCountryCode,
    string? ProviderMobile,
    string? ProviderGender,
    string? ProviderPhotoUrl,
    // The provider's registered business address (street / city / zip), resolved
    // live from the Cosmos service doc — so the detail screen needn't make a
    // second call to GET /providers/{providerId}. Null when the offering can't be
    // resolved (e.g. deregistered provider).
    string? Address = null,
    string? City = null,
    string? Zip = null);

/// <summary>The pet facts. For App bookings these come from the joined pet record;
/// for Custom walk-ins, petName + animalType come from the booking and the rest are
/// null. Breed and the medical fields (vaccination status/type/dose, prescription)
/// are joined from the pet's profile — null for Custom walk-ins and until the
/// parent fills them via PATCH /pets/{petId}/medical-info.</summary>
public sealed record PetDetailsSection(
    Guid? PetId,
    string? PetName,
    string? AnimalType,
    string? PetGender,
    string? PetImageUrl,
    string? Breed,
    string? VaccinationStatus,
    string? VaccinationType,
    string? VaccinationDose,
    string? Prescription,
    // Remaining medical-info fields from the pet's profile — null for Custom
    // walk-ins and until the parent fills them via PATCH /pets/{petId}/medical-info.
    string? SterilizationStatus = null,
    string? MedicalHistory = null,
    string? Temperament = null);

/// <summary>The money facts. <c>PricePerHour</c> is the offering's unit rate (the
/// stored per-hour price for Custom walk-ins); <c>TotalAmount</c> is rate × time;
/// <c>PawfrontFee</c> is <c>FeePercentage</c> percent of the total. Private
/// (Custom walk-in) jobs carry <c>PawfrontFee</c> = 0 and <c>FeePercentage</c> = 0
/// — Pawfront takes no commission (and hence no taxes) on off-platform jobs.
/// Pricing fields are null when the provider's offering can't be resolved (e.g.
/// deactivated service).
///
/// The payout block describes the real state of the money. <c>PayoutId</c>
/// ("PO-000123") is minted when the job COMPLETES and <c>PayoutStatus</c> is
/// 'Pending' from then until the provider records the payment, at which point it
/// becomes 'Paid'. <c>PayoutMethod</c> ('Cash' / 'Digital') and <c>PaidAtUtc</c>
/// come from the payment ledger row and are null until that happens — cash is
/// handed over off-platform, so nothing can be asserted about the method before
/// the provider confirms it. A Custom walk-in never reaches PAID (off-platform,
/// no commission), so its payout fields stay unset.
///
/// <c>PayoutStatus</c> is 'NO_PAYOUT' — terminal — once the job ends as
/// PARENT_NO_SHOW / PROVIDER_NO_SHOW / EXPIRED. Nobody performed and nobody
/// owes, so 'Pending' would be claiming money is on its way that never can be.
/// Clients should treat it as an end state, not a stage.</summary>
public sealed record PaymentDetailsSection(
    decimal? PricePerHour,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage,
    string PayoutStatus,
    string? PayoutId,
    string? PayoutMethod = null,
    DateTimeOffset? PaidAtUtc = null);

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
    Guid? PetId = null,
    // The "have I already reported this?" trio, carried identically by every
    // surface a Report button sits on — a booking (list + detail), an event
    // (list + detail) and a conversation (inbox + thread).
    //
    // MINE ONLY: true when the CALLER has an open ticket on this booking, never
    // when the counterparty does. One consequence to handle — the one-open-ticket
    // rule runs in either direction, so a caller whose counterparty already
    // reported the booking reads false here and is STILL refused with 409
    // TicketAlreadyOpen. Treat that 409 as "already reported", not as an error.
    //
    // OPEN ONLY: it goes back to false once support closes the ticket, which is
    // exactly when a fresh report is allowed again — so the flag always agrees
    // with what the server will accept.
    //
    // Both ids travel because both are needed: TicketId is the GUID the
    // .../support-tickets/{ticketId} routes take, TicketRef ("TK-000123") is what
    // a screen shows. Both null when the flag is false.
    //
    // Populated on the read paths; left false/null on create and status
    // responses, where it is false by construction anyway.
    bool IsTicketRaisedByMe = false,
    Guid? TicketId = null,
    string? TicketRef = null,
    // Whether the OTHER party on this booking is blocked, in either direction.
    // A booking outlives the block that severs the pair -- the history is real
    // and both sides keep it -- so the flag exists to let the app label an
    // existing booking rather than leave the user wondering why they can no
    // longer message or rebook.
    bool IsBlocked = false,
    // True only when the CALLER placed it: the one case where an Unblock action
    // belongs. A block placed against them reads true/false and should show a
    // neutral state -- saying more would confirm the other party acted.
    bool BlockedByMe = false);

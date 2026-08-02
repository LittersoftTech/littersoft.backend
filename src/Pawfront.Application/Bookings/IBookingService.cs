namespace Pawfront.Application.Bookings;

public interface IBookingService
{
    Task<BookingResult> CreateAsync(CreateBookingCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Provider-added private/custom booking for an unregistered walk-in
    /// customer. Counts against the same per-service capacity bucket as app
    /// bookings; the per-offering duration-rule check is skipped because the
    /// provider is in direct control of the time window.
    /// </summary>
    Task<BookingResult> CreateCustomAsync(
        CreateCustomBookingCommand command,
        CancellationToken cancellationToken);

    Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Enriched booking-detail read for the detail endpoints: the booking joined
    /// with its pet-parent + pet records, plus the friendly Job ID and live-computed
    /// payment figures (unit price, total = rate × time, and the Pawfront fee at the
    /// configured percentage). Null when the booking doesn't exist.
    /// </summary>
    Task<BookingDetailResult?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List the provider's bookings. <paramref name="date"/> narrows to a single
    /// calendar day when provided (the provider's "today" view); omit it to return
    /// the full history.
    /// </summary>
    Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a booking to a new lifecycle status and writes an audit row, both in
    /// one transaction. The actor + their id are enforced against the booking
    /// (forbidden otherwise), the status must be one the actor may set, and the
    /// booking must not already be terminal. Returns the updated booking.
    /// Throws <see cref="UnsupportedBookingStatusException"/> (unknown status),
    /// <see cref="BookingNotFoundException"/>, <see cref="BookingStatusForbiddenException"/>,
    /// <see cref="BookingStatusNotAllowedException"/>,
    /// <see cref="BookingStatusTerminalException"/>, or
    /// <see cref="BookingStatusUnchangedException"/>.
    /// </summary>
    Task<BookingResult> UpdateStatusAsync(
        UpdateBookingStatusCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Provider completes the job (IN_PROGRESS → COMPLETED; no OTP), optionally
    /// storing the pet's next consultation date (one per provider type;
    /// the type is derived from the booking's service category) and a vet
    /// prescription. Throws <see cref="NextConsultationNotSupportedException"/>
    /// (PetSitter / PetAdoptionAndSale booking),
    /// <see cref="NextConsultationRequiresPetException"/> (no linked pet), or
    /// <see cref="InvalidNextConsultationDateException"/> (past date) — all
    /// validated BEFORE the transition — plus the from-state exception
    /// (<see cref="BookingNotCompletableException"/>).
    /// </summary>
    Task<BookingResult> CompleteAsync(
        CompleteBookingCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that the parent has paid the provider for a booking (COMPLETED →
    /// PAID) and writes the payment ledger row. The amount is computed from the
    /// booking's price-locked total (the same figure the detail read shows).
    /// Throws <see cref="BookingNotFoundException"/>,
    /// <see cref="BookingStatusForbiddenException"/> (not the provider),
    /// <see cref="BookingPaymentNotAppException"/> (Custom walk-in),
    /// <see cref="BookingNotPayableException"/> (not COMPLETED),
    /// <see cref="BookingAlreadyPaidException"/>, or
    /// <see cref="BookingNotPriceableException"/> (can't resolve a price).
    /// </summary>
    Task<BookingResult> MarkPaidAsync(
        MarkBookingPaidCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full status-change audit trail for a booking, oldest-first
    /// (the seeded creation entry is first). Empty when the booking has no
    /// history (or doesn't exist) — list semantics, no exception.
    /// </summary>
    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    /// <summary>
    /// Issues (or reuses) the parent-facing start-OTP. Called when the parent opens
    /// a START_JOB booking; the returned plaintext code is read to the provider,
    /// who posts it back to move the job to IN_PROGRESS.
    /// </summary>
    Task<StartOtpResult> IssueStartOtpAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Provider taps "Start Job": moves a confirmed-equivalent booking to START_JOB
    /// and issues the parent-facing start-OTP. Allowed only on the booking's own
    /// service date and while the provider is inside their own weekly working hours.
    /// Throws <see cref="BookingNotStartableException"/> (wrong from-state),
    /// <see cref="BookingStartNotOnServiceDateException"/> (wrong day), or
    /// <see cref="BookingStartOutsideWorkingHoursException"/> (provider is closed
    /// right now).
    /// </summary>
    Task<BookingResult> StartJobAsync(StartBookingCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Provider enters the parent's start-OTP: moves the booking START_JOB →
    /// IN_PROGRESS. Throws <see cref="BookingNotStartableException"/> (not START_JOB),
    /// <see cref="InvalidStartOtpException"/>, <see cref="StartOtpExpiredException"/>,
    /// or <see cref="OtpAttemptsExceededException"/> (6th wrong attempt cancels the job).
    /// </summary>
    Task<BookingResult> VerifyStartOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken);

    /// <summary>Either party proposes a date/time change (validated, then staged).</summary>
    Task<BookingResult> RequestModificationAsync(
        RequestBookingModificationCommand command,
        CancellationToken cancellationToken);

    /// <summary>The counterparty accepts (apply) or declines (discard) the proposal.</summary>
    Task<BookingResult> RespondModificationAsync(
        RespondBookingModificationCommand command,
        CancellationToken cancellationToken);

    /// <summary>Reads the staged (pending) modification proposal, or null when none.</summary>
    Task<BookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>Records one job-completion evidence photo (provider-owned booking).</summary>
    Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId,
        Guid providerId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>Lists a booking's evidence photos, oldest-first.</summary>
    Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records (upserts) the vet's per-visit prescription for a booking — the same
    /// data optionally accepted on <see cref="CompleteAsync"/>, exposed as a
    /// standalone write so the vet can fill/edit it independently. Allowed only for
    /// the booking's provider, on a Vet service, once the job has started or
    /// completed. Throws <see cref="BookingNotFoundException"/>,
    /// <see cref="BookingPrescriptionForbiddenException"/>,
    /// <see cref="BookingPrescriptionNotVetException"/>, or
    /// <see cref="BookingPrescriptionInvalidStateException"/>.
    /// </summary>
    Task<BookingPrescriptionResult> UpsertPrescriptionAsync(
        UpsertBookingPrescriptionCommand command,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Bookings;

/// <summary>
/// Low-level SQL operations on <c>Booking.Bookings</c>. Used by <see cref="BookingService"/>
/// and indirectly by the slot service via <see cref="IDailyBookingReader"/>.
/// </summary>
public interface IBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The stored proc validates the ServiceId belongs to the provider
    /// and is active, then holds UPDLOCK + HOLDLOCK on the overlap-count query for that
    /// service and rejects the insert when concurrent bookings have already filled the
    /// requested slot.
    /// Throws <see cref="BookingCapacityExceededException"/> when full,
    /// <see cref="BookingProviderNotFoundException"/> if the provider is gone,
    /// <see cref="BookingPetParentNotFoundException"/> if the parent is gone, or
    /// <see cref="BookingServiceInvalidException"/> if the ServiceId is unknown,
    /// inactive, or not owned by the provider.
    /// </summary>
    Task<BookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        string? serviceItemCode,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? jobNotes,
        string? locationType,
        // Snapshot of the offering's unit rate at booking time (price-lock); null
        // when the caller can't resolve a price.
        decimal? pricePerHour,
        // The provider's business address for a ProviderLocation booking (resolved
        // from Cosmos + registration), snapshotted onto the booking. Null for
        // ParentLocation (the sproc snapshots the parent's address) / no location.
        ProviderAddressSnapshot? providerAddress,
        int capacity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Race-safe insert of a Source = 'Custom' booking (provider-added private
    /// job). Mirrors <see cref="CreateAsync"/> but identifies the customer via
    /// free-text fields. Same 51061 / 51062 / 51066 / 51067 typed exceptions.
    /// </summary>
    Task<BookingResult> CreateCustomAsync(
        Guid providerId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        string customerName,
        string customerMobileCountryCode,
        string customerMobile,
        string animalType,
        string petName,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string serviceLocation,
        string? customerLocation,
        decimal pricePerHour,
        string? jobNotes,
        int capacity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Full-replace edit of a Source = 'Custom' booking
    /// (<c>Booking.UpdateCustomBooking</c>). Re-checks the service and the
    /// per-service capacity only when the service or the window actually moved, so
    /// a price-only correction cannot fail because the slot has since filled.
    /// Throws <see cref="BookingNotFoundException"/> (51380),
    /// <see cref="BookingStatusForbiddenException"/> (51381),
    /// <see cref="BookingNotCustomException"/> (51382),
    /// <see cref="CustomBookingNotEditableException"/> (51383),
    /// <see cref="CustomBookingScheduleLockedException"/> (51384),
    /// <see cref="BookingServiceInvalidException"/> (51066) or
    /// <see cref="BookingCapacityExceededException"/> (51062).
    /// </summary>
    Task<BookingResult> UpdateCustomAsync(
        Guid bookingId,
        Guid providerId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        string customerName,
        string customerMobileCountryCode,
        string customerMobile,
        string animalType,
        string petName,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string serviceLocation,
        string? customerLocation,
        decimal pricePerHour,
        string? jobNotes,
        int capacity,
        CancellationToken cancellationToken);

    Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Enriched single-booking read (<c>Booking.GetBookingDetail</c>) for the
    /// booking-detail endpoints — the base row plus JobNumber, payout fields, and
    /// the joined pet-parent / pet records. Null when the booking doesn't exist.
    /// </summary>
    Task<BookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken);

    /// <summary>
    /// The parent's own bookings, each carrying the frozen-at-creation extras
    /// (cancellation-policy + selected-location snapshots) for the list cards.
    /// </summary>
    Task<IReadOnlyList<BookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same occupied windows as <see cref="GetBookingsForDateAsync"/>, but
    /// carrying booking id / job number / owning parent / status
    /// (<c>Booking.GetAgendaForDate</c>). Backs the parent-facing daily agenda.
    /// </summary>
    Task<IReadOnlyList<AgendaBookingRow>> GetAgendaForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Race-safe status change + audit insert in one transaction. Maps the
    /// sproc's typed THROWs (51120 not found, 51121 forbidden, 51122 not allowed
    /// for actor, 51123 terminal, 51124 unchanged) to the matching exceptions.
    /// </summary>
    Task<BookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        // The acting party's position. Written only for the two NO-SHOW
        // transitions; null for every other status this engine serves.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    /// <summary>Issues (or reuses) the active start-OTP for a booking.</summary>
    Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId,
        string newCode,
        int ttlMinutes,
        // Where the parent was when the code went on screen.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>
    /// Provider taps "Start Job": moves confirmed-equivalent → START_JOB (gated on
    /// the booking's service date and the provider's weekly working hours) and
    /// issues the start-OTP, atomically. Maps 51132 forbidden, 51133 not startable,
    /// 51144 not the service date, 51137 outside working hours.
    /// </summary>
    Task<BookingResult> StartJobAsync(
        Guid bookingId,
        Guid providerId,
        string newCode,
        int ttlMinutes,
        // Where the provider was when they confirmed arrival.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates the start-OTP and moves the booking START_JOB → IN_PROGRESS
    /// (6th wrong attempt cancels the job).
    /// </summary>
    Task<BookingResult> VerifyStartOtpAsync(
        Guid bookingId,
        Guid providerId,
        string otpCode,
        // Written only on the success path — a wrong code is not a job start.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the booking IN_PROGRESS → COMPLETED (no OTP). Maps 51132 forbidden,
    /// 51133 not completable.
    /// </summary>
    Task<BookingResult> CompleteAsync(
        Guid bookingId,
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips a COMPLETED booking to PAID and inserts the payment ledger row
    /// (<c>Booking.BookingPayments</c>). Maps 51160 not found, 51161 forbidden,
    /// 51162 not payable, 51163 Custom walk-in, 51164 already paid.
    /// </summary>
    Task<BookingResult> MarkPaidAsync(
        Guid bookingId,
        Guid providerId,
        decimal amount,
        decimal pawfrontFee,
        string paymentMethod,
        // Where the provider was when the cash changed hands.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>Stages a date/time-change proposal and flips the booking status.</summary>
    /// <param name="acknowledgedTerms">
    /// The provider's current terms, staged alongside the proposal when they had
    /// drifted from the booking's frozen ones and the requester confirmed them.
    /// Null leaves the booking's frozen terms alone on accept.
    /// </param>
    Task<BookingResult> RequestModificationAsync(
        Guid bookingId,
        BookingStatusActor actor,
        Guid actorId,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? note,
        BookingAcknowledgedTerms? acknowledgedTerms,
        CancellationToken cancellationToken);

    /// <summary>Reads the staged (pending) proposal, or null when none.</summary>
    Task<BookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Accepts (apply, capacity re-checked) or declines (keep) the open proposal.
    /// </summary>
    Task<BookingResult> RespondModificationAsync(
        Guid bookingId,
        BookingStatusActor actor,
        Guid actorId,
        bool accept,
        int capacity,
        string? note,
        CancellationToken cancellationToken);

    Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId,
        Guid providerId,
        string photoUrl,
        // Where the provider was when they took the photo; one row per photo.
        CapturedLocation? location,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upserts the vet's prescription for a booking (<c>Booking.UpsertBookingPrescription</c>).
    /// Maps the sproc's typed THROWs: 51290 not found → <see cref="BookingNotFoundException"/>,
    /// 51291 forbidden → <see cref="BookingPrescriptionForbiddenException"/>, 51292 not a Vet
    /// booking → <see cref="BookingPrescriptionNotVetException"/>, 51293 wrong state →
    /// <see cref="BookingPrescriptionInvalidStateException"/>.
    /// </summary>
    Task<BookingPrescriptionResult> UpsertPrescriptionAsync(
        Guid bookingId,
        Guid providerId,
        string? prescriptionText,
        bool isPetVaccinated,
        IReadOnlyList<string> vaccinations,
        CancellationToken cancellationToken);
}

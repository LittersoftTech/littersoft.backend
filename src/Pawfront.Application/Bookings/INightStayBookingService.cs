namespace Pawfront.Application.Bookings;

/// <summary>
/// Orchestrates multi-night boarding bookings (PetSitter NightStay service).
/// Validates the date range, resolves the offering (capacity + drop-off /
/// pick-up times), checks per-night closures, then delegates the race-safe
/// per-night capacity check + insert to <see cref="INightStayBookingSqlStore"/>.
/// </summary>
public interface INightStayBookingService
{
    Task<NightStayBookingResult> CreateAsync(
        CreateNightStayBookingCommand command,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Enriched night-stay booking detail — the base row plus the friendly Job ID,
    /// pet-parent + pet details, live-computed per-night pricing / Pawfront fee, the
    /// provider's service location, and cancellation policy. Null when not found.
    /// </summary>
    Task<NightStayBookingDetailResult?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> UpdateStatusAsync(
        UpdateNightStayBookingStatusCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    /// <summary>
    /// Issues (or reuses) the parent-facing start-OTP. <paramref name="location"/>
    /// is where the parent was when the code went on screen — required, which is
    /// why this is reached through a dedicated POST rather than the detail GET.
    /// See <see cref="IBookingService.IssueStartOtpAsync"/>.
    /// </summary>
    Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId,
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>Provider taps "Start Job": confirmed-equivalent → START_JOB (working-hours gate) + start-OTP.</summary>
    Task<NightStayBookingResult> StartJobAsync(StartBookingCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Provider enters the parent's start-OTP: START_JOB → IN_PROGRESS.
    /// <paramref name="location"/> is required on the request but stored only on
    /// the success path — see <see cref="IBookingService.VerifyStartOtpAsync"/>.
    /// </summary>
    Task<NightStayBookingResult> VerifyStartOtpAsync(
        Guid bookingId,
        Guid providerId,
        string otpCode,
        CapturedLocation? location,
        CancellationToken cancellationToken);

    /// <summary>Provider completes the job: IN_PROGRESS → COMPLETED (no OTP).</summary>
    Task<NightStayBookingResult> CompleteAsync(
        Guid bookingId, Guid providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the parent has paid the provider for a stay (COMPLETED → PAID)
    /// and writes the payment ledger row. The amount is the stay's price-locked
    /// total. Throws <see cref="NightStayBookingNotFoundException"/>,
    /// <see cref="BookingStatusForbiddenException"/>,
    /// <see cref="BookingNotPayableException"/>,
    /// <see cref="BookingAlreadyPaidException"/>, or
    /// <see cref="BookingNotPriceableException"/>.
    /// </summary>
    Task<NightStayBookingResult> MarkPaidAsync(
        MarkBookingPaidCommand command, CancellationToken cancellationToken);

    Task<NightStayBookingResult> RequestModificationAsync(
        RequestNightStayBookingModificationCommand command,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> RespondModificationAsync(
        RespondBookingModificationCommand command,
        CancellationToken cancellationToken);

    Task<NightStayBookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one job-completion evidence photo. <paramref name="location"/> is
    /// required and stored with it — one location row per photo.
    /// </summary>
    Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId,
        Guid providerId,
        string photoUrl,
        CapturedLocation? location,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

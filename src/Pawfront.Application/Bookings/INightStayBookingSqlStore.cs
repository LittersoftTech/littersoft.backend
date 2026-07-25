namespace Pawfront.Application.Bookings;

/// <summary>
/// Low-level SQL operations on <c>Booking.NightStayBookings</c>. Mirror of
/// <see cref="IBookingSqlStore"/> for the multi-night boarding model.
/// </summary>
public interface INightStayBookingSqlStore
{
    /// <summary>
    /// Race-safe insert. The stored proc validates the ServiceId belongs to the
    /// provider, is active, and is a NightStay service, then enforces per-night
    /// capacity under UPDLOCK + HOLDLOCK across <c>[checkInDate, checkOutDate)</c>.
    /// Maps the sproc THROWs 51230–51235 to the matching typed exceptions.
    /// </summary>
    Task<NightStayBookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        TimeOnly dropOffTime,
        TimeOnly pickUpTime,
        string? jobNotes,
        string? locationType,
        // Snapshot of the offering's per-night rate at booking time (price-lock).
        decimal? pricePerNight,
        // The provider's business address for a ProviderLocation stay (resolved from
        // Cosmos + registration), snapshotted onto the booking. Null for
        // ParentLocation (the sproc snapshots the parent's address) / no location.
        ProviderAddressSnapshot? providerAddress,
        int capacity,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Enriched single-booking read (<c>Booking.GetNightStayBookingDetail</c>) for
    /// the night-stay booking-detail endpoint — the base row plus JobNumber, payout
    /// fields, and the joined pet-parent / pet records. Null when not found.
    /// </summary>
    Task<NightStayBookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// The parent's own stays, each carrying the frozen-at-creation extras
    /// (price-locked rate + cancellation-policy + selected-location snapshots)
    /// for the list cards.
    /// </summary>
    Task<IReadOnlyList<NightStayBookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Active-stay count per night over [fromNight, toNight] inclusive
    /// (<c>Booking.GetNightStayOccupancy</c>). Backs the NightStay availability
    /// surface — every night in the range is returned, zero occupancy included.
    /// </summary>
    Task<IReadOnlyDictionary<DateOnly, int>> GetNightlyOccupancyAsync(
        Guid serviceId,
        DateOnly fromNight,
        DateOnly toNight,
        CancellationToken cancellationToken);

    /// <summary>
    /// Race-safe status change + audit insert. Maps the sproc THROWs (51240 not
    /// found, 51241 forbidden, 51242 not allowed for actor, 51243 terminal,
    /// 51244 unchanged, 51245 invalid actor/status) to the matching exceptions.
    /// </summary>
    Task<NightStayBookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId,
        string newCode,
        int ttlMinutes,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> StartJobAsync(
        Guid bookingId,
        Guid providerId,
        string newCode,
        int ttlMinutes,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> VerifyStartOtpAsync(
        Guid bookingId,
        Guid providerId,
        string otpCode,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> CompleteAsync(
        Guid bookingId,
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips a COMPLETED stay to PAID and inserts the payment ledger row. Maps
    /// 51280 not found, 51281 forbidden, 51282 not payable, 51283 already paid.
    /// </summary>
    Task<NightStayBookingResult> MarkPaidAsync(
        Guid bookingId,
        Guid providerId,
        decimal amount,
        decimal pawfrontFee,
        string paymentMethod,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> RequestModificationAsync(
        Guid bookingId,
        BookingStatusActor actor,
        Guid actorId,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        string? note,
        CancellationToken cancellationToken);

    Task<NightStayBookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> RespondModificationAsync(
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
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

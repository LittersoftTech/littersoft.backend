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

    Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
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
        CancellationToken cancellationToken);

    /// <summary>Validates the start-OTP and moves the booking to JOB_STARTED.</summary>
    Task<BookingResult> StartWithOtpAsync(
        Guid bookingId,
        Guid providerId,
        string otpCode,
        CancellationToken cancellationToken);

    /// <summary>Stages a date/time-change proposal and flips the booking status.</summary>
    Task<BookingResult> RequestModificationAsync(
        Guid bookingId,
        BookingStatusActor actor,
        Guid actorId,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? note,
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
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

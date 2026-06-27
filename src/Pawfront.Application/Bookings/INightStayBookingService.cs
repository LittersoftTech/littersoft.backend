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

    Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NightStayBookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<NightStayBookingResult> UpdateStatusAsync(
        UpdateNightStayBookingStatusCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken);
}

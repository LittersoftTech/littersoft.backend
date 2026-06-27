using Pawfront.Application.Closures;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class NightStayBookingService(
    INightStayBookingSqlStore sqlStore,
    IProviderServiceCatalog serviceCatalog,
    IPetSitterServiceRegistry petSitterRegistry,
    IProviderClosureReader closureReader) : INightStayBookingService
{
    // A stay can span at most this many nights. Matches the cap the night-stay
    // search enforces and bounds the per-night capacity walk in the sproc.
    private const int MaxStayNights = 30;

    public async Task<NightStayBookingResult> CreateAsync(
        CreateNightStayBookingCommand command,
        CancellationToken cancellationToken)
    {
        // 1. Date-range validation. The checkout day is not a stayed night, so a
        // valid stay needs at least one night (CheckOutDate strictly after CheckInDate).
        if (command.CheckOutDate <= command.CheckInDate)
        {
            throw new InvalidNightStayDatesException("CheckOutDate must be later than CheckInDate.");
        }

        var nights = command.CheckOutDate.DayNumber - command.CheckInDate.DayNumber;
        if (nights > MaxStayNights)
        {
            throw new InvalidNightStayDatesException(
                $"A night stay cannot exceed {MaxStayNights} nights.");
        }

        // 2. Resolve the service: must belong to the provider, be active, and be a
        // NightStay service.
        var service = await serviceCatalog.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null || !service.IsActive || service.ProviderId != command.ProviderId)
        {
            throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId);
        }
        if (!string.Equals(service.ServiceType, ProviderServiceTypes.NightStay, StringComparison.Ordinal))
        {
            throw new BookingNotNightStayServiceException(command.ServiceId, command.ProviderId);
        }

        // 3. Read the NightStay offering branch for capacity + drop-off / pick-up
        // times. Capacity is shop-wide (MaxPetsAtOneTime on the offering); the
        // drop-off / pick-up times are snapshotted onto the booking.
        var doc = await petSitterRegistry.GetAsync(command.ProviderId, cancellationToken);
        var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
        var nightStay = offering?.NightStay;
        if (offering is null || nightStay is null)
        {
            throw new BookingOfferingNotConfiguredException(command.ProviderId, service.ServiceCategory);
        }

        // 4. Per-night closure check. A full-day closure on this service on any
        // stayed night blocks the booking. (Partial-day closures don't apply to an
        // overnight stay — the night-stay model is date-granular, not time-window.)
        for (var night = command.CheckInDate; night < command.CheckOutDate; night = night.AddDays(1))
        {
            var closures = await closureReader.GetActiveClosuresForDateAsync(
                command.ServiceId, night, cancellationToken);
            var fullDay = closures.FirstOrDefault(c => c.IsFullDay);
            if (fullDay is not null)
            {
                throw new ProviderClosedOnDateException(command.ServiceId, night, fullDay.Reason);
            }
        }

        // 5. Hand off to SQL (race-safe per-night capacity check + insert).
        return await sqlStore.CreateAsync(
            command.ProviderId,
            command.PetParentId,
            command.PetId,
            command.ServiceId,
            service.ServiceCategory,
            service.SubCategory,
            command.CheckInDate,
            command.CheckOutDate,
            nightStay.DropOffTime,
            nightStay.PickUpTime,
            offering.MaxPetsAtOneTime,
            cancellationToken);
    }

    public Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetAsync(bookingId, cancellationToken);

    public Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
        => sqlStore.CancelAsync(bookingId, petParentId, cancellationToken);

    public Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken)
        => sqlStore.ListByProviderAsync(providerId, onDate, cancellationToken);

    public Task<IReadOnlyList<NightStayBookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
        => sqlStore.ListByPetParentAsync(petParentId, cancellationToken);

    public Task<NightStayBookingResult> UpdateStatusAsync(
        UpdateNightStayBookingStatusCommand command,
        CancellationToken cancellationToken)
    {
        // Reject an unknown status with a clean 400 before touching SQL; the sproc
        // still enforces role + transition rules authoritatively.
        var newStatus = BookingStatuses.Normalize(command.NewStatus);
        return sqlStore.UpdateStatusAsync(
            command.NightStayBookingId,
            newStatus,
            command.Actor,
            command.ActorId,
            command.Note,
            cancellationToken);
    }

    public Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
        => sqlStore.ListStatusHistoryAsync(bookingId, cancellationToken);
}

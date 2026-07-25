using Microsoft.Extensions.Options;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class NightStayBookingService(
    INightStayBookingSqlStore sqlStore,
    IProviderServiceCatalog serviceCatalog,
    IPetSitterServiceRegistry petSitterRegistry,
    IProviderClosureReader closureReader,
    IProviderDiscoveryService providerDiscovery,
    IProviderServiceLocationRegistry providerLocationRegistry,
    IOptions<PawfrontFeeOptions> feeOptions) : INightStayBookingService, INightStayOccupancyReader
{
    // A stay can span at most this many nights. Matches the cap the night-stay
    // search enforces and bounds the per-night capacity walk in the sproc.
    private const int MaxStayNights = 30;

    public Task<IReadOnlyDictionary<DateOnly, int>> GetNightlyOccupancyAsync(
        Guid serviceId,
        DateOnly fromNight,
        DateOnly toNight,
        CancellationToken cancellationToken)
        => sqlStore.GetNightlyOccupancyAsync(serviceId, fromNight, toNight, cancellationToken);

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

        // 5. Snapshot the provider's business address for a ProviderLocation stay
        // (Cosmos + registration), so a later address edit never moves this booking.
        // ParentLocation snapshots the parent's address inside the sproc.
        var locationType = BookingLocationTypes.NormalizeOptional(command.LocationType);
        var providerAddress = await BookingService.ResolveProviderAddressSnapshotAsync(
            providerDiscovery, providerLocationRegistry,
            command.ProviderId, locationType, service.ServiceCategory, cancellationToken);

        // 6. Hand off to SQL (race-safe per-night capacity check + insert). The
        // offering's per-night rate is snapshotted onto the booking (price-lock),
        // so a later rate change never re-prices this stay.
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
            TrimOrNull(command.JobNotes, maxLength: 2000, nameof(command.JobNotes)),
            locationType,
            nightStay.PricePerHour,
            providerAddress,
            offering.MaxPetsAtOneTime,
            cancellationToken);
    }

    private static string? TrimOrNull(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{field} must be {maxLength} characters or fewer.", field);
        }
        return trimmed;
    }

    public Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetAsync(bookingId, cancellationToken);

    public async Task<NightStayBookingDetailResult?> GetDetailAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var row = await sqlStore.GetDetailAsync(bookingId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        // Stayed nights = [CheckInDate, CheckOutDate); the checkout day isn't billed.
        var nights = row.CheckOutDate.DayNumber - row.CheckInDate.DayNumber;

        // Prefer the per-night rate snapshotted onto the booking at creation
        // (price-lock), falling back to the provider's current NightStay offering
        // only for legacy rows. The offering is still read for the service
        // location. The BoardingOffering's PricePerHour is the per-night rate.
        decimal? pricePerNight = row.PricePerNight;
        string? serviceLocation = null;
        var doc = await petSitterRegistry.GetAsync(row.ProviderId, cancellationToken);
        var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
        if (offering?.NightStay is not null)
        {
            pricePerNight ??= offering.NightStay.PricePerHour;
            serviceLocation = offering.ServiceLocation;
        }

        decimal? total = pricePerNight is null
            ? null
            : Math.Round(pricePerNight.Value * nights, 2, MidpointRounding.AwayFromZero);

        var feePercentage = feeOptions.Value.PawfrontFeePercentage;
        decimal? fee = total is null
            ? null
            : Math.Round(total.Value * feePercentage / 100m, 2, MidpointRounding.AwayFromZero);

        var jobId = $"PF-{row.JobNumber:D6}";

        // The provider's business summary (address/city/zip) is surfaced on
        // providerDetails on every read, and reused for the location block when a
        // legacy stay has no frozen snapshot. Fetch once (best-effort).
        var providerSummary = await BookingService.TryGetProviderSummaryAsync(
            providerDiscovery, row.ProviderId, row.ServiceCategory, cancellationToken);

        // Prefer the address frozen onto the stay at creation, falling back to live
        // resolution only for legacy rows created before snapshotting shipped.
        var location = BookingService.TrySnapshotLocation(
                row.LocationType, row.SnapshotAddressLine, row.SnapshotCity,
                row.SnapshotZipCode, row.SnapshotLatitude, row.SnapshotLongitude)
            ?? row.LocationType switch
            {
                BookingLocationTypes.ParentLocation => new BookingLocationResult(
                    BookingLocationTypes.ParentLocation,
                    row.ParentAddressLine, row.ParentCity, row.ParentZipCode,
                    row.ParentLatitude, row.ParentLongitude),
                BookingLocationTypes.ProviderLocation => await BookingService.ResolveProviderLocationAsync(
                    providerLocationRegistry, row.ProviderId, providerSummary, cancellationToken),
                _ => new BookingLocationResult(row.LocationType, null, null, null, null, null)
            };

        // The advertised cancellation policy is snapshotted onto the stay (frozen at
        // creation); a null value legitimately means "no cancellation restriction".
        return new NightStayBookingDetailResult(
            row, jobId, nights, pricePerNight, total, fee, feePercentage,
            serviceLocation, row.CancellationPolicyHours, location,
            providerSummary?.Address, providerSummary?.City, providerSummary?.Zip);
    }

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

    public Task<IReadOnlyList<NightStayBookingListItemResult>> ListByPetParentAsync(
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

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    private const int StartOtpTtlMinutes = 10;

    public Task<StartOtpResult> IssueStartOtpAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.IssueStartOtpAsync(bookingId, BookingService.GenerateOtpCode(), StartOtpTtlMinutes, cancellationToken);

    public Task<NightStayBookingResult> StartJobAsync(StartBookingCommand command, CancellationToken cancellationToken)
        => sqlStore.StartJobAsync(
            command.BookingId, command.ProviderId, BookingService.GenerateOtpCode(), StartOtpTtlMinutes, cancellationToken);

    public Task<NightStayBookingResult> VerifyStartOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
        => sqlStore.VerifyStartOtpAsync(bookingId, providerId, (otpCode ?? string.Empty).Trim(), cancellationToken);

    public Task<NightStayBookingResult> CompleteAsync(
        Guid bookingId, Guid providerId, CancellationToken cancellationToken)
        => sqlStore.CompleteAsync(bookingId, providerId, cancellationToken);

    public async Task<NightStayBookingResult> MarkPaidAsync(
        MarkBookingPaidCommand command, CancellationToken cancellationToken)
    {
        var method = BookingPaymentMethods.Normalize(command.PaymentMethod);

        // The amount is the stay's price-locked total (same figure the detail read
        // shows). The sproc enforces party + from-state (COMPLETED).
        var detail = await GetDetailAsync(command.BookingId, cancellationToken)
            ?? throw new NightStayBookingNotFoundException(command.BookingId);
        if (detail.TotalAmount is null)
        {
            throw new BookingNotPriceableException(command.BookingId);
        }

        return await sqlStore.MarkPaidAsync(
            command.BookingId,
            command.ProviderId,
            detail.TotalAmount.Value,
            detail.PawfrontFee ?? 0m,
            method,
            cancellationToken);
    }

    public async Task<NightStayBookingResult> RequestModificationAsync(
        RequestNightStayBookingModificationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CheckOutDate <= command.CheckInDate)
        {
            throw new InvalidNightStayDatesException("CheckOutDate must be later than CheckInDate.");
        }

        var nights = command.CheckOutDate.DayNumber - command.CheckInDate.DayNumber;
        if (nights > MaxStayNights)
        {
            throw new InvalidNightStayDatesException($"A night stay cannot exceed {MaxStayNights} nights.");
        }

        var booking = await sqlStore.GetAsync(command.NightStayBookingId, cancellationToken)
            ?? throw new NightStayBookingNotFoundException(command.NightStayBookingId);

        // Per-night full-day closure check on the proposed range (same as create).
        for (var night = command.CheckInDate; night < command.CheckOutDate; night = night.AddDays(1))
        {
            var closures = await closureReader.GetActiveClosuresForDateAsync(
                booking.ServiceId, night, cancellationToken);
            var fullDay = closures.FirstOrDefault(c => c.IsFullDay);
            if (fullDay is not null)
            {
                throw new ProviderClosedOnDateException(booking.ServiceId, night, fullDay.Reason);
            }
        }

        return await sqlStore.RequestModificationAsync(
            command.NightStayBookingId, command.Actor, command.ActorId,
            command.CheckInDate, command.CheckOutDate, command.Note, cancellationToken);
    }

    public async Task<NightStayBookingResult> RespondModificationAsync(
        RespondBookingModificationCommand command,
        CancellationToken cancellationToken)
    {
        var capacity = 0;
        if (command.Accept)
        {
            var booking = await sqlStore.GetAsync(command.BookingId, cancellationToken)
                ?? throw new NightStayBookingNotFoundException(command.BookingId);
            var doc = await petSitterRegistry.GetAsync(booking.ProviderId, cancellationToken);
            var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
            capacity = offering?.MaxPetsAtOneTime
                ?? throw new BookingOfferingNotConfiguredException(booking.ProviderId, booking.ServiceCategory);
        }

        return await sqlStore.RespondModificationAsync(
            command.BookingId, command.Actor, command.ActorId, command.Accept, capacity, command.Note, cancellationToken);
    }

    public Task<NightStayBookingModificationResult?> GetPendingModificationAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetPendingModificationAsync(bookingId, cancellationToken);

    public Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
        => sqlStore.AddEvidenceAsync(bookingId, providerId, photoUrl, cancellationToken);

    public Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.ListEvidenceAsync(bookingId, cancellationToken);
}

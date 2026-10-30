using Pawfront.Application.Availability;
using Microsoft.Extensions.Options;
using Pawfront.Application.Billing;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Application.Support;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class NightStayBookingService(
    INightStayBookingSqlStore sqlStore,
    IProviderServiceCatalog serviceCatalog,
    IPetSitterServiceRegistry petSitterRegistry,
    IProviderClosureReader closureReader,
    IProviderAvailabilityService availabilityService,
    IProviderDiscoveryService providerDiscovery,
    IProviderServiceLocationRegistry providerLocationRegistry,
    IBookingTermsChangeService termsChangeService,
    IInvoiceQueuePublisher invoiceQueue,
    IOptions<PawfrontFeeOptions> feeOptions,
    IMySupportTicketLookup? ticketLookup = null) : INightStayBookingService, INightStayOccupancyReader
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

        var dropOffTime = nightStay.HasFixedTimes ? nightStay.DropOffTime : command.DropOffTime;
        var pickUpTime = nightStay.HasFixedTimes ? nightStay.PickUpTime : command.PickUpTime;

        EnsureCheckInDate(command.CheckInDate);
        BookingLeadTime.EnsureFarEnoughAhead(command.CheckInDate, dropOffTime ?? TimeOnly.MinValue, DateTimeOffset.UtcNow);

        await ProviderDateAvailability.EnsureOpenAsync(
            availabilityService, closureReader, command.ProviderId, command.ServiceId,
            command.CheckInDate, command.CheckOutDate, cancellationToken);

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
            dropOffTime,
            pickUpTime,
            TrimOrNull(command.JobNotes, maxLength: 2000, nameof(command.JobNotes)),
            locationType,
            nightStay.PricePerHour,
            providerAddress,
            offering.MaxPetsAtOneTime,
            cancellationToken);
    }

    private static void EnsureCheckInDate(DateOnly date)
    {
        if (date < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InvalidNightStayDatesException("Check-in date cannot be before today.");
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
            providerSummary?.Address, providerSummary?.City, providerSummary?.Zip,
            providerSummary?.ImageUrl);
    }

    public Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
        => WithDisputeAsync(sqlStore.CancelAsync(bookingId, petParentId, cancellationToken), cancellationToken);

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

        // Mirror of BookingService.UpdateStatusAsync: a no-show cannot be recorded
        // without a position, enforced here so the legacy /status shim cannot skip it.
        var location = BookingStatuses.NoShow.Contains(newStatus)
            ? CapturedLocation.Require(command.Location, "report a no-show")
            : newStatus == BookingStatuses.PaymentNotReceived
                ? CapturedLocation.Require(command.Location, "report payment not received")
            : command.Location;
        location?.Validate();
        var noShowReason = BookingNoShowReasons.Normalize(command.NoShowReason, newStatus, command.Actor);

        return WithDisputeAsync(sqlStore.UpdateStatusAsync(
            command.NightStayBookingId,
            newStatus,
            command.Actor,
            command.ActorId,
            command.Note,
            location,
            cancellationToken, noShowReason), cancellationToken);
    }

    public Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
        => sqlStore.ListStatusHistoryAsync(bookingId, cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    private const int StartOtpTtlMinutes = 10;

    public Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId, CapturedLocation? location, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(location, "show your start code");
        return sqlStore.IssueStartOtpAsync(
            bookingId, BookingService.GenerateOtpCode(), StartOtpTtlMinutes, fix, cancellationToken);
    }

    public Task<NightStayBookingResult> StartJobAsync(StartBookingCommand command, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(command.Location, "start a job");
        return WithDisputeAsync(sqlStore.StartJobAsync(
            command.BookingId, command.ProviderId, BookingService.GenerateOtpCode(), StartOtpTtlMinutes,
            fix, cancellationToken, command.WaitForCustomer), cancellationToken);
    }

    public Task<NightStayBookingResult> VerifyStartOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CapturedLocation? location, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(location, "start a job");
        return WithDisputeAsync(sqlStore.VerifyStartOtpAsync(
            bookingId, providerId, (otpCode ?? string.Empty).Trim(), fix, cancellationToken), cancellationToken);
    }

    public Task<NightStayBookingResult> CompleteAsync(
        Guid bookingId, Guid providerId, CancellationToken cancellationToken, bool customerArrivedForCollection = false)
        => WithDisputeAsync(sqlStore.CompleteAsync(bookingId, providerId, cancellationToken, customerArrivedForCollection), cancellationToken);

    public async Task<NightStayBookingResult> MarkPaidAsync(
        MarkBookingPaidCommand command, CancellationToken cancellationToken)
    {
        var method = BookingPaymentMethods.Normalize(command.PaymentMethod);
        var location = CapturedLocation.Require(command.Location, "record a payment");

        // The amount is the stay's price-locked total (same figure the detail read
        // shows). The sproc enforces party + from-state (COMPLETED or PAYMENT_NOT_RECEIVED).
        var detail = await GetDetailAsync(command.BookingId, cancellationToken)
            ?? throw new NightStayBookingNotFoundException(command.BookingId);
        if (detail.TotalAmount is null)
        {
            throw new BookingNotPriceableException(command.BookingId);
        }

        var result = await WithDisputeAsync(sqlStore.MarkPaidAsync(
            command.BookingId,
            command.ProviderId,
            detail.TotalAmount.Value,
            detail.PawfrontFee ?? 0m,
            method,
            location,
            cancellationToken), cancellationToken);

        // Twin of the single-day path - see BookingService.MarkPaidAsync for why
        // this sits outside the transaction and why the publisher never throws.
        await invoiceQueue.PublishAsync(
            new InvoiceGenerationRequest(BookingTypes.NightStay, command.BookingId),
            cancellationToken);

        return result;
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

        EnsureCheckInDate(command.CheckInDate);
        await ProviderDateAvailability.EnsureOpenAsync(
            availabilityService, closureReader, booking.ProviderId, booking.ServiceId,
            command.CheckInDate, command.CheckOutDate, cancellationToken);

        // Provider proposals change only the schedule and preserve all frozen terms.
        // Parent proposals retain the existing drift acknowledgment behavior.
        BookingAcknowledgedTerms? acknowledgedTerms = null;
        if (command.Actor == BookingStatusActor.Parent)
        {
            var drift = await termsChangeService.GetForNightStayBookingAsync(command.NightStayBookingId, cancellationToken);
            if (drift.HasChanges)
            {
                if (!command.AcknowledgeTermsChanges)
                {
                    throw new BookingTermsChangedException(command.NightStayBookingId);
                }

                acknowledgedTerms = drift.AcknowledgedTerms;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var originalStart = BookingLeadTime.ServiceStartUtc(booking.CheckInDate, booking.DropOffTime ?? TimeOnly.MinValue);
        var proposedStart = BookingLeadTime.ServiceStartUtc(command.CheckInDate,
            (acknowledgedTerms is null ? booking.DropOffTime : acknowledgedTerms.DropOffTime) ?? TimeOnly.MinValue);
        if (BookingLeadTime.IsTooSoon(originalStart <= proposedStart ? originalStart : proposedStart, now))
            throw new BookingModificationWindowClosedException(command.NightStayBookingId);

        return await WithDisputeAsync(sqlStore.RequestModificationAsync(
            command.NightStayBookingId, command.Actor, command.ActorId,
            command.CheckInDate, command.CheckOutDate, command.Note,
            acknowledgedTerms, cancellationToken), cancellationToken);
    }

    public async Task<NightStayBookingResult> RespondModificationAsync(
        RespondBookingModificationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CancelBooking && (command.Accept || command.Actor != BookingStatusActor.Parent))
            throw new ArgumentException("Only a parent declining a provider modification can cancel the booking.");
        var capacity = 0;
        if (command.Accept)
        {
            var booking = await sqlStore.GetAsync(command.BookingId, cancellationToken)
                ?? throw new NightStayBookingNotFoundException(command.BookingId);
            var pending = await sqlStore.GetPendingModificationAsync(command.BookingId, cancellationToken)
                ?? throw new NoPendingModificationException(command.BookingId);
            EnsureCheckInDate(pending.ProposedCheckInDate);
            await ProviderDateAvailability.EnsureOpenAsync(
                availabilityService, closureReader, booking.ProviderId, booking.ServiceId,
                pending.ProposedCheckInDate, pending.ProposedCheckOutDate, cancellationToken);
            var doc = await petSitterRegistry.GetAsync(booking.ProviderId, cancellationToken);
            var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
            capacity = offering?.MaxPetsAtOneTime
                ?? throw new BookingOfferingNotConfiguredException(booking.ProviderId, booking.ServiceCategory);
        }

        return await WithDisputeAsync(sqlStore.RespondModificationAsync(
            command.BookingId, command.Actor, command.ActorId, command.Accept, capacity, command.Note, cancellationToken, command.CancelBooking), cancellationToken);
    }

    public Task<NightStayBookingModificationResult?> GetPendingModificationAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetPendingModificationAsync(bookingId, cancellationToken);

    public Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId, Guid providerId, string photoUrl, CapturedLocation? location,
        CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(location, "attach job evidence");
        return sqlStore.AddEvidenceAsync(bookingId, providerId, photoUrl, fix, cancellationToken);
    }

    public Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.ListEvidenceAsync(bookingId, cancellationToken);

    private async Task<NightStayBookingResult> WithDisputeAsync(
        Task<NightStayBookingResult> bookingTask, CancellationToken cancellationToken)
    {
        var booking = await bookingTask;
        if (ticketLookup is null) return booking;
        var tickets = await ticketLookup.GetAsync(
            SupportRaisedByTypes.Provider, booking.ProviderId, cancellationToken);
        return booking with
        {
            IsDisputeRaised = tickets.IsDisputeRaisedForBooking(BookingTypes.NightStay, booking.NightStayBookingId)
        };
    }
}

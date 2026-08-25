using Microsoft.Extensions.Options;
using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.Offerings;
using Pawfront.Application.ParentPets;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class BookingService(
    IBookingSqlStore sqlStore,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IProviderClosureReader closureReader,
    IPetNextConsultationStore nextConsultationStore,
    IProviderDiscoveryService providerDiscovery,
    IProviderServiceLocationRegistry providerLocationRegistry,
    IBookingTermsChangeService termsChangeService,
    IOptions<PawfrontFeeOptions> feeOptions) : IBookingService, IDailyBookingReader, IDailyAgendaReader
{
    public async Task<BookingResult> CreateAsync(
        CreateBookingCommand command,
        CancellationToken cancellationToken)
    {
        if (command.StartTime >= command.EndTime)
        {
            throw new InvalidBookingTimeException("StartTime must be earlier than EndTime.");
        }

        // 1. Resolve the service: capacity + duration rule + ownership check.
        var resolution = await offeringResolver.ResolveAsync(command.ServiceId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotFound => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Inactive => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.NotConfigured nc => throw new BookingOfferingNotConfiguredException(nc.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r when r.ProviderId != command.ProviderId
                => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        // 1a. NightStay is a multi-night boarding service keyed by a check-in /
        // check-out date range, not a single-day time window — it can't be booked
        // through this path. Send the caller to the dedicated night-stay endpoint.
        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            throw new BookingNightStayUseDedicatedEndpointException();
        }

        // 1b. PetGroomer: resolve the requested menu-item code and replace the
        // offering's duration with the item's per-groomer duration. Other
        // categories ignore ServiceItemCode entirely — duration comes from the
        // offering itself (DayCare/NightStay minimum, Trainer/Vet fixed).
        string? serviceItemCode = null;
        // Snapshot of the offering's unit rate, captured now so a later price edit
        // by the provider never re-prices this booking. For DayCare/Vet/Trainer the
        // rate is the offering price; for grooming it's the chosen menu item's price.
        decimal? snapshotUnitPrice = offering.Price;
        if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
        {
            if (string.IsNullOrWhiteSpace(command.ServiceItemCode))
            {
                throw new BookingGroomingItemCodeRequiredException();
            }

            serviceItemCode = command.ServiceItemCode!.Trim();
            var itemResolution = await offeringResolver.ResolveGroomingItemAsync(
                command.ProviderId, serviceItemCode, cancellationToken);

            switch (itemResolution)
            {
                case GroomingItemResolution.OfferingMissing:
                    throw new BookingOfferingNotConfiguredException(command.ProviderId, offering.ServiceCategory);
                case GroomingItemResolution.NotOffered no:
                    throw new BookingGroomingItemNotOfferedException(command.ProviderId, no.Code);
                case GroomingItemResolution.Inactive ia:
                    throw new BookingGroomingItemInactiveException(command.ProviderId, ia.Code);
                case GroomingItemResolution.Resolved ri:
                    offering = offering with
                    {
                        DurationHours = (decimal)ri.DurationMinutes / 60m,
                        IsDurationFixed = true
                    };
                    snapshotUnitPrice = ri.Price;
                    break;
                default:
                    throw new InvalidOperationException("Unknown grooming item resolution.");
            }
        }

        // 2. Validate the booking duration matches the offering rule.
        var bookingDurationSpan = command.EndTime - command.StartTime;
        var bookingDurationHours = (decimal)bookingDurationSpan.TotalHours;
        ValidateDuration(bookingDurationHours, offering);

        // 2b. The service must start at least the minimum lead time from now. The
        // slot surfaces already hide these windows, so reaching here means a stale
        // slot list or a hand-rolled request.
        BookingLeadTime.EnsureFarEnoughAhead(command.BookingDate, command.StartTime, DateTimeOffset.UtcNow);

        // 3. Validate the requested window fits inside the provider's weekly availability.
        await ValidateAgainstAvailabilityAsync(
            command.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 3b. Reject if a closure on THIS service covers the requested window.
        await ValidateAgainstClosuresAsync(
            command.ServiceId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 4. Snapshot the provider's business address when the service happens at
        // their place (price-lock sibling): resolve it now (Cosmos + registration)
        // and hand it to the sproc so a later address edit never moves this booking.
        // ParentLocation snapshots the parent's address inside the sproc.
        var locationType = BookingLocationTypes.NormalizeOptional(command.LocationType);
        var providerAddress = await ResolveProviderAddressSnapshotAsync(
            command.ProviderId, locationType, offering.ServiceCategory, cancellationToken);

        // 5. Hand off to the SQL sproc (capacity check + insert is race-safe there).
        return await sqlStore.CreateAsync(
            command.ProviderId,
            command.PetParentId,
            command.PetId,
            command.ServiceId,
            offering.ServiceCategory,
            offering.SubCategory,
            serviceItemCode,
            command.BookingDate,
            command.StartTime,
            command.EndTime,
            TrimOrNull(command.JobNotes, maxLength: 2000, nameof(command.JobNotes)),
            locationType,
            snapshotUnitPrice,
            providerAddress,
            offering.Capacity,
            cancellationToken);
    }

    /// <summary>
    /// Resolves the provider's business address to snapshot onto a ProviderLocation
    /// booking (Cosmos service doc + registration coordinates). Returns null for any
    /// other location type — the sproc snapshots the parent's address instead.
    /// Best-effort: a failed lookup yields a snapshot with null sub-fields, so the
    /// detail read falls back to live resolution for that booking. Shared with the
    /// night-stay create flow.
    /// </summary>
    internal static async Task<ProviderAddressSnapshot?> ResolveProviderAddressSnapshotAsync(
        IProviderDiscoveryService providerDiscovery,
        IProviderServiceLocationRegistry providerLocationRegistry,
        Guid providerId,
        string? locationType,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        if (locationType != BookingLocationTypes.ProviderLocation)
        {
            return null;
        }

        var summary = await TryGetProviderSummaryAsync(
            providerDiscovery, providerId, serviceCategory, cancellationToken);
        var location = await ResolveProviderLocationAsync(
            providerLocationRegistry, providerId, summary, cancellationToken);
        return new ProviderAddressSnapshot(
            location.AddressLine, location.City, location.ZipCode, location.Latitude, location.Longitude);
    }

    private Task<ProviderAddressSnapshot?> ResolveProviderAddressSnapshotAsync(
        Guid providerId, string? locationType, string serviceCategory, CancellationToken cancellationToken)
        => ResolveProviderAddressSnapshotAsync(
            providerDiscovery, providerLocationRegistry, providerId, locationType, serviceCategory, cancellationToken);

    /// <summary>
    /// Builds the location block from the booking's frozen snapshot columns, or null
    /// when the booking predates snapshotting (all snapshot fields null) so the caller
    /// falls back to live resolution. Shared with the night-stay detail flow.
    /// </summary>
    internal static BookingLocationResult? TrySnapshotLocation(
        string? locationType,
        string? addressLine,
        string? city,
        string? zipCode,
        decimal? latitude,
        decimal? longitude)
    {
        if (addressLine is null && city is null && zipCode is null
            && latitude is null && longitude is null)
        {
            return null;
        }

        return new BookingLocationResult(locationType, addressLine, city, zipCode, latitude, longitude);
    }

    public async Task<BookingResult> UpdateCustomAsync(
        UpdateCustomBookingCommand command,
        CancellationToken cancellationToken)
    {
        // 0. Field-level validation — identical to the create path, since this is a
        // full replace of the same form.
        var customerName = Required(command.CustomerName, nameof(command.CustomerName), maxLength: 200);
        var countryCode = Required(command.CustomerMobileCountryCode, nameof(command.CustomerMobileCountryCode), maxLength: 8);
        var mobile = Required(command.CustomerMobile, nameof(command.CustomerMobile), maxLength: 32);
        var petName = Required(command.PetName, nameof(command.PetName), maxLength: 100);
        var animalType = NormaliseAnimalType(command.AnimalType);
        var serviceLocation = NormaliseServiceLocation(command.ServiceLocation);
        var customerLocation = NormaliseCustomerLocation(command.CustomerLocation, serviceLocation);
        var jobNotes = TrimOrNull(command.JobNotes, maxLength: 2000, nameof(command.JobNotes));

        if (command.PricePerHour < 0m)
        {
            throw new ArgumentException(
                "PricePerHour must be greater than or equal to 0.",
                nameof(command.PricePerHour));
        }

        if (command.StartTime >= command.EndTime)
        {
            throw new InvalidBookingTimeException("StartTime must be earlier than EndTime.");
        }

        // 1. Read the booking first. Not just to fail early: the calendar gates
        // below must run ONLY when the window actually moved, and that comparison
        // needs the stored values. Without it, a provider correcting a price would
        // be refused because their working hours had changed since — which is the
        // opposite of what this endpoint is for. SQL re-checks all of it under
        // UPDLOCK, so this read is for shaping the request, not for safety.
        var existing = await sqlStore.GetAsync(command.BookingId, cancellationToken)
            ?? throw new BookingNotFoundException(command.BookingId);

        if (existing.ProviderId != command.ProviderId)
        {
            throw new BookingStatusForbiddenException(command.BookingId);
        }

        // "Custom" as a literal, matching how the endpoints and the sprocs spell it.
        if (!string.Equals(existing.Source, "Custom", StringComparison.Ordinal))
        {
            throw new BookingNotCustomException(command.BookingId);
        }

        if (BookingStatuses.Cancelled.Contains(existing.Status))
        {
            throw new CustomBookingNotEditableException(command.BookingId, existing.Status);
        }

        var scheduleChanged =
            existing.ServiceId != command.ServiceId
            || existing.BookingDate != command.BookingDate
            || existing.StartTime != command.StartTime
            || existing.EndTime != command.EndTime;

        if (scheduleChanged && !string.Equals(existing.Status, BookingStatuses.Confirmed, StringComparison.Ordinal))
        {
            throw new CustomBookingScheduleLockedException(command.BookingId, existing.Status);
        }

        // 2. Resolve the service for capacity + ownership. Done even on a
        // price-only edit, because the capacity figure has to be handed to SQL
        // either way and resolving it here keeps one code path.
        var resolution = await offeringResolver.ResolveAsync(command.ServiceId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotFound => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Inactive => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.NotConfigured nc => throw new BookingOfferingNotConfiguredException(nc.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r when r.ProviderId != command.ProviderId
                => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            throw new BookingNightStayUseDedicatedEndpointException();
        }

        // 3. Calendar gates, only for a window that actually moved — same two the
        // create path runs, and still no booking-lead-time check: a walk-in is
        // recorded as it happens, so demanding two hours' notice on an edit would
        // be as unusable as it would be on the create.
        if (scheduleChanged)
        {
            await ValidateAgainstAvailabilityAsync(
                command.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

            await ValidateAgainstClosuresAsync(
                command.ServiceId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);
        }

        // 4. Hand off to SQL (race-safe re-check + update).
        return await sqlStore.UpdateCustomAsync(
            command.BookingId,
            command.ProviderId,
            command.ServiceId,
            offering.ServiceCategory,
            offering.SubCategory,
            customerName,
            countryCode,
            mobile,
            animalType,
            petName,
            command.BookingDate,
            command.StartTime,
            command.EndTime,
            serviceLocation,
            customerLocation,
            command.PricePerHour,
            jobNotes,
            offering.Capacity,
            cancellationToken);
    }

    public async Task<BookingResult> CreateCustomAsync(
        CreateCustomBookingCommand command,
        CancellationToken cancellationToken)
    {
        // 0. Field-level validation (free-text / enum shape).
        var customerName = Required(command.CustomerName, nameof(command.CustomerName), maxLength: 200);
        var countryCode = Required(command.CustomerMobileCountryCode, nameof(command.CustomerMobileCountryCode), maxLength: 8);
        var mobile = Required(command.CustomerMobile, nameof(command.CustomerMobile), maxLength: 32);
        var petName = Required(command.PetName, nameof(command.PetName), maxLength: 100);
        var animalType = NormaliseAnimalType(command.AnimalType);
        var serviceLocation = NormaliseServiceLocation(command.ServiceLocation);
        var customerLocation = NormaliseCustomerLocation(command.CustomerLocation, serviceLocation);
        var jobNotes = TrimOrNull(command.JobNotes, maxLength: 2000, nameof(command.JobNotes));

        if (command.PricePerHour < 0m)
        {
            throw new ArgumentException(
                "PricePerHour must be greater than or equal to 0.",
                nameof(command.PricePerHour));
        }

        if (command.StartTime >= command.EndTime)
        {
            throw new InvalidBookingTimeException("StartTime must be earlier than EndTime.");
        }

        // 1. Resolve the service for capacity + ownership. We DON'T enforce the
        // offering's duration rule for custom bookings — the provider has set the
        // time window themselves, so the only binding constraint is capacity.
        var resolution = await offeringResolver.ResolveAsync(command.ServiceId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotFound => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Inactive => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.NotConfigured nc => throw new BookingOfferingNotConfiguredException(nc.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r when r.ProviderId != command.ProviderId
                => throw new BookingServiceInvalidException(command.ServiceId, command.ProviderId),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        // NightStay is a multi-night boarding service — it has no single-day
        // representation, so it can't be recorded as a custom walk-in here either.
        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            throw new BookingNightStayUseDedicatedEndpointException();
        }

        // 2. Scheduling: working hours, break, closure. Same gates as the app
        // booking path so the provider's calendar stays consistent — EXCEPT the
        // booking lead time, which deliberately does not apply here. A custom
        // booking records a walk-in the provider is serving now; requiring it to
        // be entered two hours ahead would make the feature unusable.
        await ValidateAgainstAvailabilityAsync(
            command.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        await ValidateAgainstClosuresAsync(
            command.ServiceId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 3. Hand off to SQL (race-safe capacity check + insert).
        return await sqlStore.CreateCustomAsync(
            command.ProviderId,
            command.ServiceId,
            offering.ServiceCategory,
            offering.SubCategory,
            customerName,
            countryCode,
            mobile,
            animalType,
            petName,
            command.BookingDate,
            command.StartTime,
            command.EndTime,
            serviceLocation,
            customerLocation,
            command.PricePerHour,
            jobNotes,
            offering.Capacity,
            cancellationToken);
    }

    public Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetAsync(bookingId, cancellationToken);

    public async Task<BookingDetailResult?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var row = await sqlStore.GetDetailAsync(bookingId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var durationHours = (decimal)(row.EndTime - row.StartTime).TotalHours;
        var isCustom = string.Equals(row.Source, "Custom", StringComparison.Ordinal);

        // Custom walk-ins carry their own per-hour price + window AND their own
        // service-location ("MyLocation"/"CustomerLocation"). App bookings are
        // priced live from the provider's current offering and inherit the
        // offering's service-location setting (the "where" the service happens).
        decimal? unitPrice;
        decimal? total;
        string? serviceLocation;
        if (isCustom)
        {
            (unitPrice, total) = ResolveCustomPricing(row, durationHours);
            serviceLocation = row.ServiceLocation;
        }
        else
        {
            // The offering supplies the service location (and, for legacy rows
            // without a price snapshot, the live rate). A price-locked booking is
            // still priced from its snapshot even if the offering was since
            // deactivated — that's the whole point of the snapshot.
            var resolution = await offeringResolver.ResolveAsync(row.ServiceId, cancellationToken);
            var offering = resolution as OfferingResolution.Resolved;
            serviceLocation = offering?.ServiceLocation;
            (unitPrice, total) = await ResolveAppPricingAsync(offering, row, durationHours, cancellationToken);
        }

        // Private (Custom walk-in) jobs are arranged off-platform — Pawfront takes
        // no commission on them, so the fee facts are zero (not null: the payment
        // block still renders, it just shows no fee/taxes).
        var feePercentage = isCustom ? 0m : feeOptions.Value.PawfrontFeePercentage;
        decimal? fee = total is null
            ? null
            : Math.Round(total.Value * feePercentage / 100m, 2, MidpointRounding.AwayFromZero);

        // The provider's business summary (name/address/city/zip) is needed for the
        // providerDetails address block on every read, and — when a legacy row has no
        // frozen location snapshot — for the location block too. Fetch it once
        // (best-effort) and reuse.
        var providerSummary = await TryGetProviderSummaryAsync(
            providerDiscovery, row.ProviderId, row.ServiceCategory, cancellationToken);

        // Prefer the "where does the service happen" address frozen onto the booking
        // at creation, falling back to live resolution (parent profile / provider
        // business address) only for legacy rows created before snapshotting shipped.
        var location = TrySnapshotLocation(
                row.LocationType, row.SnapshotAddressLine, row.SnapshotCity,
                row.SnapshotZipCode, row.SnapshotLatitude, row.SnapshotLongitude)
            ?? row.LocationType switch
            {
                BookingLocationTypes.ParentLocation => new BookingLocationResult(
                    BookingLocationTypes.ParentLocation,
                    row.ParentAddressLine, row.ParentCity, row.ParentZipCode,
                    row.ParentLatitude, row.ParentLongitude),
                BookingLocationTypes.ProviderLocation => await ResolveProviderLocationAsync(
                    providerLocationRegistry, row.ProviderId, providerSummary, cancellationToken),
                _ => new BookingLocationResult(row.LocationType, null, null, null, null, null)
            };

        var jobId = $"PF-{row.JobNumber:D6}";
        // The advertised cancellation policy is snapshotted onto the booking (frozen
        // at creation); a null value legitimately means "no cancellation restriction".
        return new BookingDetailResult(
            row, jobId, unitPrice, total, fee, feePercentage,
            serviceLocation, row.CancellationPolicyHours, location,
            providerSummary?.Address, providerSummary?.City, providerSummary?.Zip,
            providerSummary?.ImageUrl);
    }

    /// <summary>
    /// Best-effort read of the provider's business summary (name / address / city /
    /// zip from the Cosmos service doc). Returns null on any failure so a booking
    /// detail never fails on the provider-side lookup. Shared with the night-stay
    /// detail flow.
    /// </summary>
    internal static async Task<ProviderSummary?> TryGetProviderSummaryAsync(
        IProviderDiscoveryService providerDiscovery,
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await providerDiscovery.GetSummaryAsync(providerId, serviceCategory, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the ProviderLocation block from the (pre-fetched, best-effort)
    /// provider business <paramref name="providerSummary"/> — its street / city /
    /// zip — plus the registered coordinates (SQL registration row). A
    /// failed/missing registration lookup yields null lat/lng rather than failing
    /// the detail read. Shared with the night-stay detail flow. The summary is
    /// resolved by the caller via <see cref="TryGetProviderSummaryAsync"/> so it can
    /// be reused for the providerDetails address block on the same read.
    /// </summary>
    internal static async Task<BookingLocationResult> ResolveProviderLocationAsync(
        IProviderServiceLocationRegistry providerLocationRegistry,
        Guid providerId,
        ProviderSummary? providerSummary,
        CancellationToken cancellationToken)
    {
        var addressLine = providerSummary?.Address;
        var city = providerSummary?.City;
        var zip = providerSummary?.Zip;
        decimal? latitude = null;
        decimal? longitude = null;

        try
        {
            var registration = await providerLocationRegistry.GetByProviderIdAsync(providerId, cancellationToken);
            if (registration is not null)
            {
                latitude = registration.Latitude;
                longitude = registration.Longitude;
            }
        }
        catch
        {
            // Best-effort — coordinates degrade to null.
        }

        return new BookingLocationResult(
            BookingLocationTypes.ProviderLocation, addressLine, city, zip, latitude, longitude);
    }

    private static (decimal? UnitPrice, decimal? Total) ResolveCustomPricing(
        BookingDetailRow row, decimal durationHours)
    {
        var unit = row.PricePerHour;
        var total = unit is null
            ? (decimal?)null
            : Math.Round(unit.Value * durationHours, 2, MidpointRounding.AwayFromZero);
        return (unit, total);
    }

    private async Task<(decimal? UnitPrice, decimal? Total)> ResolveAppPricingAsync(
        OfferingResolution.Resolved? offering,
        BookingDetailRow row,
        decimal durationHours,
        CancellationToken cancellationToken)
    {
        // Prefer the price-locked snapshot captured on the booking at creation
        // time, so a later rate change (or a full deactivation) by the provider
        // never re-prices this booking. Only DayCare (PetSitter) bills per hour;
        // every other single-day service (Vet / Trainer / grooming item) is a flat
        // fee. Legacy rows with no snapshot fall through to the live offering below.
        if (row.PricePerHour is decimal snapshot)
        {
            var perHour = string.Equals(
                row.ServiceCategory, nameof(ProviderServiceCategory.PetSitter), StringComparison.Ordinal);
            var snapshotTotal = perHour ? snapshot * durationHours : snapshot;
            return (snapshot, Math.Round(snapshotTotal, 2, MidpointRounding.AwayFromZero));
        }

        // No snapshot (legacy row) AND the offering is gone — can't price it.
        if (offering is null)
        {
            return (null, null);
        }

        // PetGroomer: the unit price is per menu item, resolved from the booking's
        // own ServiceItemCode. Grooming is a flat per-service charge (one item).
        if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
        {
            if (string.IsNullOrWhiteSpace(row.ServiceItemCode))
            {
                return (null, null);
            }

            var itemResolution = await offeringResolver.ResolveGroomingItemAsync(
                row.ProviderId, row.ServiceItemCode!, cancellationToken);
            return itemResolution is GroomingItemResolution.Resolved item
                ? (item.Price, Math.Round(item.Price, 2, MidpointRounding.AwayFromZero))
                : (null, null);
        }

        if (offering.Price is null)
        {
            return (null, null);
        }

        var unitPrice = offering.Price.Value;
        // Fixed-duration services (Vet/Trainer) bill a flat fee; min-duration
        // services (DayCare) bill the per-hour rate × the booked hours.
        var total = offering.IsDurationFixed ? unitPrice : unitPrice * durationHours;
        return (unitPrice, Math.Round(total, 2, MidpointRounding.AwayFromZero));
    }

    public Task<BookingResult> CancelAsync(Guid bookingId, Guid petParentId, CancellationToken cancellationToken)
        => sqlStore.CancelAsync(bookingId, petParentId, cancellationToken);

    public Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken)
        => sqlStore.ListByProviderAsync(providerId, date, cancellationToken);

    public Task<IReadOnlyList<BookingListItemResult>> ListByPetParentAsync(Guid petParentId, CancellationToken cancellationToken)
        => sqlStore.ListByPetParentAsync(petParentId, cancellationToken);

    public Task<BookingResult> UpdateStatusAsync(
        UpdateBookingStatusCommand command,
        CancellationToken cancellationToken)
    {
        // Reject an unknown status with a clean 400 before touching SQL; the
        // sproc still enforces role + transition rules authoritatively.
        var newStatus = BookingStatuses.Normalize(command.NewStatus);

        // A no-show is one of the evidenced moments, so it cannot be recorded
        // without a position. Enforced HERE rather than in the endpoints because
        // this is the single point both hosts' /no-show routes and the legacy
        // /status shim funnel through — the shim can set a no-show too, and
        // gating only the dedicated routes would have left it as a way around
        // the capture. Every other status this engine serves passes null.
        var location = BookingStatuses.NoShow.Contains(newStatus)
            ? CapturedLocation.Require(command.Location, "report a no-show")
            : command.Location;
        location?.Validate();

        return sqlStore.UpdateStatusAsync(
            command.BookingId,
            newStatus,
            command.Actor,
            command.ActorId,
            command.Note,
            location,
            cancellationToken);
    }

    public async Task<BookingResult> CompleteAsync(
        CompleteBookingCommand command,
        CancellationToken cancellationToken)
    {
        // Validate the consultation + prescription requests BEFORE transitioning,
        // so a bad body doesn't leave the booking completed with the extras
        // silently dropped. Fetch the booking once if either rides along.
        string? consultationType = null;
        Guid? petId = null;

        BookingResult? booking = null;
        if (command.NextConsultationDate is not null || command.Prescription is not null)
        {
            booking = await sqlStore.GetAsync(command.BookingId, cancellationToken)
                ?? throw new BookingNotFoundException(command.BookingId);
        }

        if (command.NextConsultationDate is { } nextDate)
        {
            consultationType = booking!.ServiceCategory switch
            {
                nameof(ProviderServiceCategory.PetGroomer) => "Groomer",
                nameof(ProviderServiceCategory.Vet) => "Vet",
                nameof(ProviderServiceCategory.PetTrainer) => "Trainer",
                _ => throw new NextConsultationNotSupportedException(booking.ServiceCategory)
            };

            petId = booking.PetId
                ?? throw new NextConsultationRequiresPetException(command.BookingId);

            if (nextDate < DateOnly.FromDateTime(DateTime.UtcNow.Date))
            {
                throw new InvalidNextConsultationDateException(nextDate);
            }
        }

        // A prescription is Vet-only. The sproc re-checks (defense-in-depth) after
        // the transition, but reject early so we don't complete then fail.
        if (command.Prescription is not null
            && !string.Equals(
                booking!.ServiceCategory, nameof(ProviderServiceCategory.Vet), StringComparison.Ordinal))
        {
            throw new BookingPrescriptionNotVetException(command.BookingId);
        }

        // No OTP gates completion — the sproc enforces party + from-state
        // (IN_PROGRESS) and flips the booking to COMPLETED with an audit row.
        var result = await sqlStore.CompleteAsync(
            command.BookingId,
            command.ProviderId,
            cancellationToken);

        // Extras are stored only once the transition succeeded — the status engine
        // is the authority on party/from-state rules.
        if (consultationType is not null && petId is not null)
        {
            await nextConsultationStore.UpsertAsync(
                petId.Value, consultationType, command.NextConsultationDate!.Value, cancellationToken);
        }

        if (command.Prescription is { } prescription)
        {
            await sqlStore.UpsertPrescriptionAsync(
                command.BookingId,
                command.ProviderId,
                NormalizePrescriptionText(prescription.PrescriptionText),
                prescription.IsPetVaccinated,
                NormalizeVaccinations(prescription.Vaccinations),
                cancellationToken);
        }

        return result;
    }

    public async Task<BookingResult> MarkPaidAsync(
        MarkBookingPaidCommand command,
        CancellationToken cancellationToken)
    {
        var method = BookingPaymentMethods.Normalize(command.PaymentMethod);
        var location = CapturedLocation.Require(command.Location, "record a payment");

        // The amount is the booking's price-locked total (same figure the detail
        // read shows) — a single source of truth for pricing. The sproc enforces
        // party / from-state (COMPLETED) / App-only and rejects otherwise.
        var detail = await GetDetailAsync(command.BookingId, cancellationToken)
            ?? throw new BookingNotFoundException(command.BookingId);
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
            location,
            cancellationToken);
    }

    public Task<BookingPrescriptionResult> UpsertPrescriptionAsync(
        UpsertBookingPrescriptionCommand command,
        CancellationToken cancellationToken)
        => sqlStore.UpsertPrescriptionAsync(
            command.BookingId,
            command.ProviderId,
            NormalizePrescriptionText(command.PrescriptionText),
            command.IsPetVaccinated,
            NormalizeVaccinations(command.Vaccinations),
            cancellationToken);

    private static string? NormalizePrescriptionText(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > 4000)
        {
            throw new ArgumentException(
                "PrescriptionText must be 4000 characters or fewer.", nameof(value));
        }
        return trimmed;
    }

    private static IReadOnlyList<string> NormalizeVaccinations(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<string>();
        }

        var cleaned = values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();

        foreach (var vaccine in cleaned)
        {
            if (vaccine.Length > 200)
            {
                throw new ArgumentException(
                    "Each vaccination name must be 200 characters or fewer.", nameof(values));
            }
        }

        return cleaned;
    }

    public Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
        => sqlStore.ListStatusHistoryAsync(bookingId, cancellationToken);

    public Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken)
        => sqlStore.GetBookingsForDateAsync(serviceId, date, cancellationToken);

    public Task<IReadOnlyList<AgendaBookingRow>> GetAgendaForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken)
        => sqlStore.GetAgendaForDateAsync(serviceId, date, cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    private const int StartOtpTtlMinutes = 10;

    public Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId, CapturedLocation? location, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(location, "show your start code");
        return sqlStore.IssueStartOtpAsync(bookingId, GenerateOtpCode(), StartOtpTtlMinutes, fix, cancellationToken);
    }

    public Task<BookingResult> StartJobAsync(StartBookingCommand command, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(command.Location, "start a job");
        return sqlStore.StartJobAsync(
            command.BookingId, command.ProviderId, GenerateOtpCode(), StartOtpTtlMinutes, fix, cancellationToken);
    }

    public Task<BookingResult> VerifyStartOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CapturedLocation? location, CancellationToken cancellationToken)
    {
        var fix = CapturedLocation.Require(location, "start a job");
        return sqlStore.VerifyStartOtpAsync(
            bookingId, providerId, (otpCode ?? string.Empty).Trim(), fix, cancellationToken);
    }

    public async Task<BookingResult> RequestModificationAsync(
        RequestBookingModificationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.StartTime >= command.EndTime)
        {
            throw new InvalidBookingTimeException("StartTime must be earlier than EndTime.");
        }

        var booking = await sqlStore.GetAsync(command.BookingId, cancellationToken)
            ?? throw new BookingNotFoundException(command.BookingId);

        // Resolve the booked service's capacity + duration rule, exactly as the
        // create flow does, so the proposed window is validated the same way.
        var resolution = await offeringResolver.ResolveAsync(booking.ServiceId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotFound => throw new BookingServiceInvalidException(booking.ServiceId, booking.ProviderId),
            OfferingResolution.Inactive => throw new BookingServiceInvalidException(booking.ServiceId, booking.ProviderId),
            OfferingResolution.NotConfigured nc => throw new BookingOfferingNotConfiguredException(nc.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        if (offering.ServiceType == ProviderServiceTypes.NightStay)
        {
            throw new BookingNightStayUseDedicatedEndpointException();
        }

        // Editing is limited to date/time — the booked service item can't change.
        // For a groomer, validate the proposed window against the EXISTING item's
        // duration (resolved from the booking's own ServiceItemCode).
        if (offering.ServiceType == ProviderServiceTypes.GroomingSession
            && !string.IsNullOrWhiteSpace(booking.ServiceItemCode))
        {
            var itemResolution = await offeringResolver.ResolveGroomingItemAsync(
                booking.ProviderId, booking.ServiceItemCode!, cancellationToken);

            offering = itemResolution switch
            {
                GroomingItemResolution.Resolved ri => offering with
                {
                    DurationHours = (decimal)ri.DurationMinutes / 60m,
                    IsDurationFixed = true
                },
                // Item no longer offered/active — fall back to the offering's own
                // duration rule rather than blocking a date/time change.
                _ => offering
            };
        }

        var durationHours = (decimal)(command.EndTime - command.StartTime).TotalHours;
        ValidateDuration(durationHours, offering);

        await ValidateAgainstAvailabilityAsync(
            booking.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);
        await ValidateAgainstClosuresAsync(
            booking.ServiceId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // The booking froze the provider's terms at creation. If they have drifted
        // since, the requester must have seen and confirmed the new ones — the app
        // shows them from the terms-changes endpoint. An un-acknowledged request
        // against a drifted booking is rejected rather than silently proposing a
        // schedule under terms the requester never saw.
        var drift = await termsChangeService.GetForBookingAsync(command.BookingId, cancellationToken);
        BookingAcknowledgedTerms? acknowledgedTerms = null;
        if (drift.HasChanges)
        {
            if (!command.AcknowledgeTermsChanges)
            {
                throw new BookingTermsChangedException(command.BookingId);
            }

            acknowledgedTerms = drift.AcknowledgedTerms;
        }

        return await sqlStore.RequestModificationAsync(
            command.BookingId, command.Actor, command.ActorId,
            command.BookingDate, command.StartTime, command.EndTime,
            command.Note, acknowledgedTerms, cancellationToken);
    }

    public Task<BookingModificationResult?> GetPendingModificationAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetPendingModificationAsync(bookingId, cancellationToken);

    public async Task<BookingResult> RespondModificationAsync(
        RespondBookingModificationCommand command,
        CancellationToken cancellationToken)
    {
        // Capacity is only needed when accepting (the proposed window is applied);
        // resolve it from the booked service so the sproc can re-check race-safely.
        var capacity = 0;
        if (command.Accept)
        {
            var booking = await sqlStore.GetAsync(command.BookingId, cancellationToken)
                ?? throw new BookingNotFoundException(command.BookingId);
            var resolution = await offeringResolver.ResolveAsync(booking.ServiceId, cancellationToken);
            capacity = resolution is OfferingResolution.Resolved r
                ? r.Capacity
                : throw new BookingOfferingNotConfiguredException(booking.ProviderId, booking.ServiceCategory);
        }

        return await sqlStore.RespondModificationAsync(
            command.BookingId, command.Actor, command.ActorId, command.Accept, capacity, command.Note, cancellationToken);
    }

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

    internal static string GenerateOtpCode()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static void ValidateDuration(decimal durationHours, OfferingResolution.Resolved offering)
    {
        if (offering.IsDurationFixed && durationHours != offering.DurationHours)
        {
            throw new InvalidBookingTimeException(
                $"This service requires a fixed booking duration of {offering.DurationHours} hours.");
        }

        if (!offering.IsDurationFixed && durationHours < offering.DurationHours)
        {
            throw new InvalidBookingTimeException(
                $"This service requires a minimum booking duration of {offering.DurationHours} hours.");
        }
    }

    private async Task ValidateAgainstClosuresAsync(
        Guid serviceId,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var closures = await closureReader.GetActiveClosuresForDateAsync(serviceId, bookingDate, cancellationToken);
        foreach (var closure in closures)
        {
            // Full-day closure on this service blocks any booking on the date.
            if (closure.IsFullDay)
            {
                throw new ProviderClosedOnDateException(serviceId, bookingDate, closure.Reason);
            }

            // Partial-day closure: standard half-open overlap test.
            if (closure.StartTime!.Value < endTime && closure.EndTime!.Value > startTime)
            {
                throw new ProviderClosedOnDateException(serviceId, bookingDate, closure.Reason);
            }
        }
    }

    private static readonly IReadOnlySet<string> AllowedAnimalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dog", "Cat", "Hamster", "GuineaPig"
    };

    private static readonly IReadOnlySet<string> AllowedServiceLocations = new HashSet<string>(StringComparer.Ordinal)
    {
        "MyLocation", "CustomerLocation"
    };

    private static string Required(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"{field} is required.", field);
        }
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{field} must be {maxLength} characters or fewer.", field);
        }
        return trimmed;
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

    private static string NormaliseAnimalType(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("AnimalType is required.", nameof(value));
        }
        if (!AllowedAnimalTypes.Contains(trimmed))
        {
            throw new ArgumentException(
                $"AnimalType '{trimmed}' is not supported. Use Dog, Cat, Hamster, or GuineaPig.",
                nameof(value));
        }
        return trimmed;
    }

    private static string NormaliseServiceLocation(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("ServiceLocation is required.", nameof(value));
        }
        if (!AllowedServiceLocations.Contains(trimmed))
        {
            throw new ArgumentException(
                $"ServiceLocation '{trimmed}' is not supported. Use MyLocation or CustomerLocation.",
                nameof(value));
        }
        return trimmed;
    }

    private static string? NormaliseCustomerLocation(string? value, string serviceLocation)
    {
        var trimmed = value?.Trim();
        var hasValue = !string.IsNullOrEmpty(trimmed);

        if (serviceLocation == "CustomerLocation")
        {
            if (!hasValue)
            {
                throw new ArgumentException(
                    "CustomerLocation is required when ServiceLocation is 'CustomerLocation'.",
                    nameof(value));
            }
            if (trimmed!.Length > 500)
            {
                throw new ArgumentException(
                    "CustomerLocation must be 500 characters or fewer.",
                    nameof(value));
            }
            return trimmed;
        }

        // MyLocation: must NOT carry an address.
        if (hasValue)
        {
            throw new ArgumentException(
                "CustomerLocation must be omitted when ServiceLocation is 'MyLocation'.",
                nameof(value));
        }
        return null;
    }

    private async Task ValidateAgainstAvailabilityAsync(
        Guid providerId,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var weekly = await availabilityService.GetAsync(providerId, cancellationToken);
        var dayOfWeek = (int)bookingDate.DayOfWeek;
        var day = weekly.Days.FirstOrDefault(d => d.DayOfWeek == dayOfWeek);

        if (day is null || !day.IsOpen || day.StartTime is null || day.EndTime is null)
        {
            throw new InvalidBookingTimeException(
                $"Provider is not open on {bookingDate:yyyy-MM-dd}.");
        }

        if (startTime < day.StartTime || endTime > day.EndTime)
        {
            throw new InvalidBookingTimeException(
                $"Booking window must fit inside the provider's working hours " +
                $"{day.StartTime}-{day.EndTime} on {bookingDate:yyyy-MM-dd}.");
        }

        if (day.BreakStartTime is not null && day.BreakEndTime is not null)
        {
            // Booking cannot straddle the break.
            if (startTime < day.BreakEndTime && endTime > day.BreakStartTime)
            {
                throw new InvalidBookingTimeException(
                    $"Booking window overlaps the provider's break " +
                    $"({day.BreakStartTime}-{day.BreakEndTime}) on {bookingDate:yyyy-MM-dd}.");
            }
        }
    }
}

using Microsoft.Extensions.Options;
using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.Offerings;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class BookingService(
    IBookingSqlStore sqlStore,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IProviderClosureReader closureReader,
    IOptions<PawfrontFeeOptions> feeOptions) : IBookingService, IDailyBookingReader
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
        if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
        {
            if (string.IsNullOrWhiteSpace(command.ServiceItemCode))
            {
                throw new BookingGroomingItemCodeRequiredException();
            }

            serviceItemCode = command.ServiceItemCode!.Trim();
            var itemResolution = await offeringResolver.ResolveGroomingItemAsync(
                command.ProviderId, serviceItemCode, cancellationToken);

            offering = itemResolution switch
            {
                GroomingItemResolution.OfferingMissing
                    => throw new BookingOfferingNotConfiguredException(command.ProviderId, offering.ServiceCategory),
                GroomingItemResolution.NotOffered no
                    => throw new BookingGroomingItemNotOfferedException(command.ProviderId, no.Code),
                GroomingItemResolution.Inactive ia
                    => throw new BookingGroomingItemInactiveException(command.ProviderId, ia.Code),
                GroomingItemResolution.Resolved ri => offering with
                {
                    DurationHours = (decimal)ri.DurationMinutes / 60m,
                    IsDurationFixed = true
                },
                _ => throw new InvalidOperationException("Unknown grooming item resolution.")
            };
        }

        // 2. Validate the booking duration matches the offering rule.
        var bookingDurationSpan = command.EndTime - command.StartTime;
        var bookingDurationHours = (decimal)bookingDurationSpan.TotalHours;
        ValidateDuration(bookingDurationHours, offering);

        // 3. Validate the requested window fits inside the provider's weekly availability.
        await ValidateAgainstAvailabilityAsync(
            command.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 3b. Reject if a closure on THIS service covers the requested window.
        await ValidateAgainstClosuresAsync(
            command.ServiceId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 4. Hand off to the SQL sproc (capacity check + insert is race-safe there).
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
        // booking path so the provider's calendar stays consistent.
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

        // Custom walk-ins carry their own per-hour price + window; App bookings
        // are priced live from the provider's current offering.
        var (unitPrice, total) = string.Equals(row.Source, "Custom", StringComparison.Ordinal)
            ? ResolveCustomPricing(row, durationHours)
            : await ResolveAppPricingAsync(row, durationHours, cancellationToken);

        var feePercentage = feeOptions.Value.PawfrontFeePercentage;
        decimal? fee = total is null
            ? null
            : Math.Round(total.Value * feePercentage / 100m, 2, MidpointRounding.AwayFromZero);

        var jobId = $"PF-{row.JobNumber:D6}";
        return new BookingDetailResult(row, jobId, unitPrice, total, fee, feePercentage);
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
        BookingDetailRow row, decimal durationHours, CancellationToken cancellationToken)
    {
        var resolution = await offeringResolver.ResolveAsync(row.ServiceId, cancellationToken);
        if (resolution is not OfferingResolution.Resolved offering)
        {
            // Service deactivated / not configured — can't price it; leave null.
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

    public Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(Guid petParentId, CancellationToken cancellationToken)
        => sqlStore.ListByPetParentAsync(petParentId, cancellationToken);

    public Task<BookingResult> UpdateStatusAsync(
        UpdateBookingStatusCommand command,
        CancellationToken cancellationToken)
    {
        // Reject an unknown status with a clean 400 before touching SQL; the
        // sproc still enforces role + transition rules authoritatively.
        var newStatus = BookingStatuses.Normalize(command.NewStatus);
        return sqlStore.UpdateStatusAsync(
            command.BookingId,
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

    public Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken)
        => sqlStore.GetBookingsForDateAsync(serviceId, date, cancellationToken);

    // --- Job lifecycle: start-OTP, evidence, modifications ------------------

    private const int StartOtpTtlMinutes = 10;

    public Task<StartOtpResult> IssueStartOtpAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.IssueStartOtpAsync(bookingId, GenerateOtpCode(), StartOtpTtlMinutes, cancellationToken);

    public Task<BookingResult> StartWithOtpAsync(StartBookingCommand command, CancellationToken cancellationToken)
        => sqlStore.StartWithOtpAsync(command.BookingId, command.ProviderId, (command.OtpCode ?? string.Empty).Trim(), cancellationToken);

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

        return await sqlStore.RequestModificationAsync(
            command.BookingId, command.Actor, command.ActorId,
            command.BookingDate, command.StartTime, command.EndTime,
            command.Note, cancellationToken);
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
        Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
        => sqlStore.AddEvidenceAsync(bookingId, providerId, photoUrl, cancellationToken);

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

using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

internal sealed class BookingService(
    IBookingSqlStore sqlStore,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService,
    IProviderClosureReader closureReader) : IBookingService, IDailyBookingReader
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

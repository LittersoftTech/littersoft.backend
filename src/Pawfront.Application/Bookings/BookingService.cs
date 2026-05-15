using Pawfront.Application.Availability;
using Pawfront.Application.Offerings;

namespace Pawfront.Application.Bookings;

internal sealed class BookingService(
    IBookingSqlStore sqlStore,
    IProviderOfferingResolver offeringResolver,
    IProviderAvailabilityService availabilityService) : IBookingService, IDailyBookingReader
{
    public async Task<BookingResult> CreateAsync(
        CreateBookingCommand command,
        CancellationToken cancellationToken)
    {
        if (command.StartTime >= command.EndTime)
        {
            throw new InvalidBookingTimeException("StartTime must be earlier than EndTime.");
        }

        // 1. Resolve the provider's category, sub-category, capacity, and duration rule.
        var resolution = await offeringResolver.ResolveAsync(command.ProviderId, cancellationToken);
        var offering = resolution switch
        {
            OfferingResolution.NotRegistered => throw new BookingProviderNotRegisteredException(command.ProviderId),
            OfferingResolution.NotConfigured nc => throw new BookingOfferingNotConfiguredException(command.ProviderId, nc.ServiceCategory),
            OfferingResolution.Resolved r => r,
            _ => throw new InvalidOperationException("Unknown offering resolution.")
        };

        // 2. Validate the booking duration matches the offering rule.
        var bookingDurationSpan = command.EndTime - command.StartTime;
        var bookingDurationHours = (decimal)bookingDurationSpan.TotalHours;
        ValidateDuration(bookingDurationHours, offering);

        // 3. Validate the requested window fits inside the provider's weekly availability.
        await ValidateAgainstAvailabilityAsync(
            command.ProviderId, command.BookingDate, command.StartTime, command.EndTime, cancellationToken);

        // 4. Hand off to the SQL sproc (capacity check + insert is race-safe there).
        return await sqlStore.CreateAsync(
            command.ProviderId,
            command.PetParentId,
            offering.ServiceCategory,
            offering.SubCategory,
            command.BookingDate,
            command.StartTime,
            command.EndTime,
            offering.Capacity,
            cancellationToken);
    }

    public Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
        => sqlStore.GetAsync(bookingId, cancellationToken);

    public Task<BookingResult> CancelAsync(Guid bookingId, Guid petParentId, CancellationToken cancellationToken)
        => sqlStore.CancelAsync(bookingId, petParentId, cancellationToken);

    public Task<IReadOnlyList<BookingResult>> ListByProviderAsync(Guid providerId, CancellationToken cancellationToken)
        => sqlStore.ListByProviderAsync(providerId, cancellationToken);

    public Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(Guid petParentId, CancellationToken cancellationToken)
        => sqlStore.ListByPetParentAsync(petParentId, cancellationToken);

    public Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken)
        => sqlStore.GetBookingsForDateAsync(providerId, date, cancellationToken);

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

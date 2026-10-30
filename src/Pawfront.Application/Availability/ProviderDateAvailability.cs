using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;

namespace Pawfront.Application.Availability;

internal static class ProviderDateAvailability
{
    public static async Task EnsureOpenAsync(
        IProviderAvailabilityService availability, IProviderClosureReader closures,
        Guid providerId, Guid serviceId, DateOnly first, DateOnly last,
        CancellationToken cancellationToken)
    {
        var weekly = await availability.GetAsync(providerId, cancellationToken);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (!weekly.Days.Any(d => d.DayOfWeek == (int)day.DayOfWeek && d.IsOpen))
                throw new ProviderClosedOnDateException(serviceId, day, "Provider is closed on this weekday.");

            var timeOff = await closures.GetActiveClosuresForDateAsync(serviceId, day, cancellationToken);
            if (timeOff.Count > 0)
                throw new ProviderClosedOnDateException(serviceId, day, timeOff[0].Reason);
        }
    }
}

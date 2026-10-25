using Pawfront.Api.Endpoints;
using Pawfront.Application.Bookings;
using Pawfront.Contracts.Bookings;

namespace Pawfront.Api.Auth;

/// <summary>Checks both the URL's provider and the booking against the JWT owner.</summary>
internal sealed class ProviderBookingAccessFilter(bool nightStay) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var providerId = await http.RequestServices.GetRequiredService<ICurrentProviderContext>()
            .GetProviderIdAsync(ct);
        if (providerId is null)
            return ApiResults.Forbidden("ProviderProfileNotCompleted", "A provider profile is required to access bookings.");

        if (http.GetRouteValue("providerId") is string rawProvider
            && (!Guid.TryParse(rawProvider, out var routeProvider) || routeProvider != providerId))
            return Forbidden();

        if (http.GetRouteValue("bookingId") is string rawBooking && Guid.TryParse(rawBooking, out var bookingId))
        {
            Guid? bookingProvider;
            if (nightStay)
                bookingProvider = (await http.RequestServices.GetRequiredService<INightStayBookingService>()
                    .GetAsync(bookingId, ct))?.ProviderId;
            else
                bookingProvider = (await http.RequestServices.GetRequiredService<IBookingService>()
                    .GetAsync(bookingId, ct))?.ProviderId;

            if (bookingProvider is null)
                return ApiResults.NotFound(nightStay ? "NightStayBookingNotFound" : "BookingNotFound", "Booking was not found.");
            if (bookingProvider != providerId) return Forbidden();
        }

        // Check the complete batch before allowing any cancellation side effect.
        if (context.Arguments.OfType<BulkCancelBookingsRequest>().FirstOrDefault()?.Bookings is { Count: <= 50 } bookings)
        {
            foreach (var item in bookings.Where(b => b is not null).DistinctBy(b => (b.BookingType, b.BookingId)))
            {
                Guid? owner = null;
                if (string.Equals(item.BookingType, BookingTypes.NightStay, StringComparison.OrdinalIgnoreCase))
                    owner = (await http.RequestServices.GetRequiredService<INightStayBookingService>().GetAsync(item.BookingId, ct))?.ProviderId;
                else if (string.Equals(item.BookingType, BookingTypes.SingleDay, StringComparison.OrdinalIgnoreCase))
                    owner = (await http.RequestServices.GetRequiredService<IBookingService>().GetAsync(item.BookingId, ct))?.ProviderId;
                if (owner.HasValue && owner != providerId) return Forbidden();
            }
        }

        return await next(context);
    }

    private static IResult Forbidden() => ApiResults.Forbidden("Forbidden", "You can only access your own bookings.");
}

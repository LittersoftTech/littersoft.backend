using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Events;
using Pawfront.Application.Offerings;
using Pawfront.Application.Onboarding;

namespace Pawfront.Application;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddPawfrontApplication(this IServiceCollection services)
    {
        services.TryAddScoped<IProviderOnboardingStatusService, ProviderOnboardingStatusService>();
        services.TryAddScoped<IEventService, EventService>();
        services.TryAddScoped<IEventBookingService, EventBookingService>();
        services.TryAddScoped<IProviderOfferingResolver, ProviderOfferingResolver>();
        services.TryAddScoped<IProviderAvailabilitySlotService, ProviderAvailabilitySlotService>();

        // BookingService implements two interfaces — register once, expose both.
        services.TryAddScoped<BookingService>();
        services.TryAddScoped<IBookingService>(sp => sp.GetRequiredService<BookingService>());
        services.TryAddScoped<IDailyBookingReader>(sp => sp.GetRequiredService<BookingService>());

        // ProviderClosureService also implements two interfaces (service + narrow reader).
        services.TryAddScoped<ProviderClosureService>();
        services.TryAddScoped<IProviderClosureService>(sp => sp.GetRequiredService<ProviderClosureService>());
        services.TryAddScoped<IProviderClosureReader>(sp => sp.GetRequiredService<ProviderClosureService>());

        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Events;
using Pawfront.Application.Offerings;
using Pawfront.Application.Onboarding;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.Providers;

namespace Pawfront.Application;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddPawfrontApplication(this IServiceCollection services)
    {
        services.TryAddScoped<IProviderOnboardingStatusService, ProviderOnboardingStatusService>();
        services.TryAddScoped<IPetParentOnboardingStatusService, PetParentOnboardingStatusService>();
        services.TryAddScoped<IProviderPublicProfileService, ProviderPublicProfileService>();
        services.TryAddScoped<IEventService, EventService>();
        services.TryAddScoped<IEventBookingService, EventBookingService>();
        services.TryAddScoped<IProviderOfferingResolver, ProviderOfferingResolver>();
        services.TryAddScoped<IProviderAvailabilitySlotService, ProviderAvailabilitySlotService>();
        services.TryAddScoped<IProviderWindowAvailabilityChecker, ProviderWindowAvailabilityChecker>();
        services.TryAddScoped<IProviderSearchService, ProviderSearchService>();

        // BookingService implements two interfaces — register once, expose both.
        services.TryAddScoped<BookingService>();
        services.TryAddScoped<IBookingService>(sp => sp.GetRequiredService<BookingService>());
        services.TryAddScoped<IDailyBookingReader>(sp => sp.GetRequiredService<BookingService>());

        // Multi-night boarding (PetSitter NightStay) — separate from the
        // single-day BookingService because a stay is a check-in/check-out date
        // range, not a single-day time window.
        services.TryAddScoped<INightStayBookingService, NightStayBookingService>();

        // Enriches a parent's "my bookings" cards with provider + service details.
        services.TryAddScoped<IParentBookingEnrichmentService, ParentBookingEnrichmentService>();

        // ProviderClosureService also implements two interfaces (service + narrow reader).
        services.TryAddScoped<ProviderClosureService>();
        services.TryAddScoped<IProviderClosureService>(sp => sp.GetRequiredService<ProviderClosureService>());
        services.TryAddScoped<IProviderClosureReader>(sp => sp.GetRequiredService<ProviderClosureService>());

        return services;
    }
}

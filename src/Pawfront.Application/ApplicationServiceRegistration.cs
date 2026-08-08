using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Earnings;
using Pawfront.Application.Events;
using Pawfront.Application.Notifications;
using Pawfront.Application.Offerings;
using Pawfront.Application.Onboarding;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.Providers;
using Pawfront.Application.Reviews;

namespace Pawfront.Application;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddPawfrontApplication(this IServiceCollection services)
    {
        services.TryAddScoped<IProviderOnboardingStatusService, ProviderOnboardingStatusService>();
        services.TryAddScoped<IPetParentOnboardingStatusService, PetParentOnboardingStatusService>();
        services.TryAddScoped<IProviderPublicProfileService, ProviderPublicProfileService>();

        // Account delete spans SQL + Cosmos + Blob, so it is orchestrated here.
        services.TryAddScoped<IProviderAccountService, ProviderAccountService>();
        // The pet-parent twin — SQL + Blob only (a parent owns no Cosmos doc).
        services.TryAddScoped<IParentAccountService, ParentAccountService>();
        services.TryAddScoped<IEventService, EventService>();
        services.TryAddScoped<IEventBookingService, EventBookingService>();
        services.TryAddScoped<IProviderOfferingResolver, ProviderOfferingResolver>();
        services.TryAddScoped<IProviderAvailabilitySlotService, ProviderAvailabilitySlotService>();
        services.TryAddScoped<IProviderDailyAgendaService, ProviderDailyAgendaService>();
        services.TryAddScoped<IProviderWindowAvailabilityChecker, ProviderWindowAvailabilityChecker>();
        services.TryAddScoped<IProviderSearchService, ProviderSearchService>();

        // BookingService implements two interfaces — register once, expose both.
        services.TryAddScoped<BookingService>();
        services.TryAddScoped<IBookingService>(sp => sp.GetRequiredService<BookingService>());
        services.TryAddScoped<IDailyBookingReader>(sp => sp.GetRequiredService<BookingService>());
        services.TryAddScoped<IDailyAgendaReader>(sp => sp.GetRequiredService<BookingService>());

        // Multi-night boarding (PetSitter NightStay) — separate from the
        // single-day BookingService because a stay is a check-in/check-out date
        // range, not a single-day time window. Also implements the narrow
        // occupancy reader that feeds the slot service's per-night availability.
        services.TryAddScoped<NightStayBookingService>();
        services.TryAddScoped<INightStayBookingService>(sp => sp.GetRequiredService<NightStayBookingService>());
        services.TryAddScoped<INightStayOccupancyReader>(sp => sp.GetRequiredService<NightStayBookingService>());

        // Enriches a parent's "my bookings" cards with provider + service details.
        services.TryAddScoped<IParentBookingEnrichmentService, ParentBookingEnrichmentService>();

        // Diffs a booking's frozen-at-creation terms against the provider's current
        // ones, for the "these changed since you booked" confirmation sheet.
        services.TryAddScoped<IBookingTermsChangeService, BookingTermsChangeService>();

        // Earnings / spend reporting. Read-only aggregates over the booking tables
        // and the payment ledger — the two sides read one shared SQL definition of
        // a booking's amount, so a provider's "earned" and a parent's "spent" can
        // never disagree.
        services.TryAddScoped<IProviderEarningsService, ProviderEarningsService>();
        services.TryAddScoped<IParentSpendService, ParentSpendService>();

        // Booking reviews, both directions. The eligibility gate (COMPLETED or PAID,
        // correct party, App booking) is enforced in Review.UpsertBookingReview, so
        // this layer only validates input and caps the page size.
        services.TryAddScoped<IBookingReviewService, BookingReviewService>();

        // Composes booking push notifications (who to tell, with what parameters).
        // The copy itself lives in NotificationTemplateCatalog and is rendered by
        // the dispatcher, not here.
        services.TryAddScoped<IBookingNotificationService, BookingNotificationService>();

        // ProviderClosureService also implements two interfaces (service + narrow reader).
        services.TryAddScoped<ProviderClosureService>();
        services.TryAddScoped<IProviderClosureService>(sp => sp.GetRequiredService<ProviderClosureService>());
        services.TryAddScoped<IProviderClosureReader>(sp => sp.GetRequiredService<ProviderClosureService>());

        return services;
    }
}

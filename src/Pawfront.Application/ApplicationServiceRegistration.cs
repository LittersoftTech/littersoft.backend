using Pawfront.Application.Blocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Chat;
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
using Pawfront.Application.Support;

namespace Pawfront.Application;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddPawfrontApplication(this IServiceCollection services)
    {
        // Chat. Composes the SQL thread index, the Cosmos message store and the
        // live fan-out, so the REST endpoints and the SignalR hub take one path.
        // The realtime publisher is TryAdd'd as a no-op: only the chat host has a
        // hub to register in its place, and the other hosts must still resolve.
        services.TryAddScoped<IChatService, ChatService>();
        services.TryAddScoped<IChatRealtimePublisher, NullChatRealtimePublisher>();
        services.TryAddSingleton<IChatPushDispatcher, NullChatPushDispatcher>();

        services.TryAddScoped<IProviderOnboardingStatusService, ProviderOnboardingStatusService>();
        services.TryAddScoped<IPetParentOnboardingStatusService, PetParentOnboardingStatusService>();
        services.TryAddScoped<IProviderPublicProfileService, ProviderPublicProfileService>();

        // Account delete spans SQL + Cosmos + Blob, so it is orchestrated here.
        services.TryAddScoped<IProviderAccountService, ProviderAccountService>();
        // The pet-parent twin — SQL + Blob only (a parent owns no Cosmos doc).
        services.TryAddScoped<IParentAccountService, ParentAccountService>();
        // Fills in the provider photo (Cosmos) and price block on the unfinished
        // jobs that refuse an account or per-pet delete — neither is reachable
        // from the sproc that decides the refusal.
        services.TryAddScoped<IPendingJobEnricher, PendingJobEnricher>();
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

        // Cancels several bookings of either kind in one call. Pure orchestration
        // over the two booking services — every item takes the ordinary
        // per-booking transition, so there is no bulk SQL path to drift from it.
        services.TryAddScoped<IBulkBookingCancellationService, BulkBookingCancellationService>();

        // Blocking, product-wide. Depends on the bulk cancellation above rather
        // than reimplementing a cancel, so a block ends the pair's unfinished jobs
        // through the ordinary transitions -- party check, audit row, freed
        // capacity and the counterparty's push all included.
        services.TryAddScoped<IBlockService, BlockService>();
        // Who the caller is, for the discovery block filter. TryAdd, so a host
        // that registers its own BEFORE this call keeps it; a host with no
        // discovery surface gets the no-op and filters nothing.
        services.TryAddScoped<ICurrentBlockParty, NullCurrentBlockParty>();

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

        // Support tickets — "Report Incident" on a booking and "Report Chat" on a
        // conversation, both directions. Composes the SQL row (parties, subject,
        // status, and therefore the legal holds) with the Cosmos narrative; the
        // party check and the one-open-ticket-per-subject rule are enforced in
        // Support.CreateTicket, so this layer validates input and sequences the two
        // stores.
        services.TryAddScoped<ISupportTicketService, SupportTicketService>();

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

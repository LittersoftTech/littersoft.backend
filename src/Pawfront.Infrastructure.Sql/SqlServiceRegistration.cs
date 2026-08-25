using Pawfront.Application.Blocks;
using Pawfront.Infrastructure.Sql.Blocks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Chat;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.DeviceTokens;
using Pawfront.Application.Earnings;
using Pawfront.Application.Events;
using Pawfront.Application.Notifications;
using Pawfront.Application.Onboarding;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Application.ParentPhotos;
using Pawfront.Application.Policies;
using Pawfront.Application.ProviderBanners;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.ProviderPhotos;
using Pawfront.Application.ProviderServiceBanners;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Reviews;
using Pawfront.Application.Support;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Infrastructure.Sql.Availability;
using Pawfront.Infrastructure.Sql.Bookings;
using Pawfront.Infrastructure.Sql.Chat;
using Pawfront.Infrastructure.Sql.Closures;
using Pawfront.Infrastructure.Sql.DeviceTokens;
using Pawfront.Infrastructure.Sql.Earnings;
using Pawfront.Infrastructure.Sql.Events;
using Pawfront.Infrastructure.Sql.Notifications;
using Pawfront.Infrastructure.Sql.Onboarding;
using Pawfront.Infrastructure.Sql.ParentOnboarding;
using Pawfront.Infrastructure.Sql.ParentPets;
using Pawfront.Infrastructure.Sql.ParentPhotos;
using Pawfront.Infrastructure.Sql.Policies;
using Pawfront.Infrastructure.Sql.ProviderBanners;
using Pawfront.Infrastructure.Sql.ProviderOnboarding;
using Pawfront.Infrastructure.Sql.ProviderPhotos;
using Pawfront.Infrastructure.Sql.ProviderServiceBanners;
using Pawfront.Infrastructure.Sql.Providers;
using Pawfront.Infrastructure.Sql.ProviderServices;
using Pawfront.Infrastructure.Sql.Reviews;
using Pawfront.Infrastructure.Sql.Support;

namespace Pawfront.Infrastructure.Sql;

public static class SqlServiceRegistration
{
    public static IServiceCollection AddPawfrontSqlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IProviderService, InMemoryProviderService>();
        services.TryAddSingleton<IProviderMobileOtpSender, NoOpProviderMobileOtpSender>();
        services.TryAddSingleton<IPetParentMobileOtpSender, NoOpPetParentMobileOtpSender>();

        var sqlConnectionString = configuration.GetConnectionString("SqlServer");
        var useKeyVault = configuration.GetValue("AzureKeyVault:Enabled", true);

        if (string.IsNullOrWhiteSpace(sqlConnectionString) && !useKeyVault)
        {
            services.AddSingleton<IProviderOnboardingService, InMemoryProviderOnboardingService>();
            services.AddSingleton<IProviderServiceLocationRegistry, InMemoryProviderServiceLocationRegistry>();
            services.AddSingleton<IProviderPolicyService, InMemoryProviderPolicyService>();
            services.AddSingleton<IProviderOnboardingStatusReader, InMemoryProviderOnboardingStatusReader>();
            services.AddSingleton<IEventSqlStore, InMemoryEventStore>();
            services.AddSingleton<IProviderAvailabilityService, InMemoryProviderAvailabilityService>();
            // InMemoryBookingStore reads night-stay occupancy from the concrete
            // night-stay store (slot capacity), so both resolve the SAME singleton.
            services.AddSingleton<InMemoryNightStayBookingStore>();
            services.AddSingleton<IBookingSqlStore, InMemoryBookingStore>();
            services.AddSingleton<INightStayBookingSqlStore>(sp => sp.GetRequiredService<InMemoryNightStayBookingStore>());
            services.AddSingleton<IProviderClosureSqlStore, InMemoryProviderClosureStore>();
            // No location-event table to write to. Validates the fix and drops it,
            // so the flows that now carry one stay usable without a database.
            services.AddSingleton<IBookingLocationService, NullBookingLocationService>();
            services.AddSingleton<IProviderServiceCatalog, InMemoryProviderServiceCatalog>();
            services.AddSingleton<IPetNextConsultationStore, InMemoryPetNextConsultationStore>();
            services.AddSingleton<IProviderNameReader, NullProviderNameReader>();
            services.AddSingleton<IProviderContactReader, NullProviderContactReader>();
            // The in-memory provider store has no IsActive concept, so this one
            // reports everything active — otherwise discovery and all five
            // searches would come back empty on a dev machine without SQL.
            services.AddSingleton<IProviderActiveStatusReader, NullProviderActiveStatusReader>();
            // Earnings are aggregates over the booking tables joined to the payment
            // ledger, neither of which the in-memory stores keep — report zeros
            // rather than 500ing the reporting screens.
            services.AddSingleton<IProviderEarningsStore, NullProviderEarningsStore>();
            services.AddSingleton<IParentSpendStore, NullParentSpendStore>();
            // The chat thread's "View Jobs" list reads the two booking tables the
            // in-memory stores do not keep in a form this can query — report an
            // empty history rather than 500ing the screen, the same posture as
            // earnings above.
            services.AddSingleton<IParentProviderBookingReader, NullParentProviderBookingReader>();
            // No outbox table to write to — log and drop, same posture as the
            // booking sweeps having no in-memory equivalent.
            services.AddSingleton<INotificationPublisher, NullNotificationPublisher>();
            services.AddSingleton<IInstantNotificationSender, NullInstantNotificationSender>();

            // Reviews DO work in-memory (unlike earnings, which report zeros): this
            // is a write flow, so a store that accepted a submit and never showed it
            // again would make the feature untestable without SQL. It cannot enforce
            // the COMPLETED/PAID gate though — that needs the booking tables.
            services.AddSingleton<InMemoryBookingReviewStore>();
            services.AddSingleton<IBookingReviewStore>(sp => sp.GetRequiredService<InMemoryBookingReviewStore>());
            services.AddSingleton<IPetParentRatingReader>(sp => sp.GetRequiredService<InMemoryBookingReviewStore>());

            // Support tickets, functional for the same reason. Three things it cannot do,
            // all for want of the other tables: derive the counterparty from the
            // booking or conversation, enforce the party check, and check that a
            // reported event exists. The one-open-ticket-per-subject rules and the photo
            // cap ARE enforced, since all of them are answerable from what it holds.
            services.AddSingleton<InMemorySupportTicketStore>();
            services.AddSingleton<ISupportTicketStore>(sp => sp.GetRequiredService<InMemorySupportTicketStore>());
            services.AddSingleton<ISupportLegalHoldReader>(sp => sp.GetRequiredService<InMemorySupportTicketStore>());
            services.AddSingleton<IMySupportTicketLookup>(sp => sp.GetRequiredService<InMemorySupportTicketStore>());

            // Blocking, functional for the same reason. It cannot return the pair's
            // unfinished jobs, having no sight of the booking tables, so a block placed
            // here severs the pair but cancels nothing.
            services.AddSingleton<InMemoryBlockStore>();
            services.AddSingleton<IBlockStore>(sp => sp.GetRequiredService<InMemoryBlockStore>());
            services.AddSingleton<IMyBlockLookup>(sp => sp.GetRequiredService<InMemoryBlockStore>());

            // Chat works in-memory for the same reason reviews do — it is a write
            // flow, and a store that swallowed messages would make it untestable
            // without SQL. Two limits: no outbox, so no pushes are ever queued;
            // and no profile tables, so the counterparty is unnamed.
            services.AddSingleton<InMemoryChatPresenceStore>();
            services.AddSingleton<IChatPresenceStore>(sp => sp.GetRequiredService<InMemoryChatPresenceStore>());
            services.AddSingleton<IChatConversationStore>(sp =>
                new InMemoryChatConversationStore(
                    sp.GetRequiredService<IChatPresenceStore>(),
                    sp.GetRequiredService<IBlockStore>()));

            // Both hosts' token services share one store here; each host only ever
            // resolves its own interface, so they never actually mix.
            services.AddSingleton<InMemoryDeviceTokenStore>();
            services.AddSingleton<IProviderDeviceTokenService, InMemoryProviderDeviceTokenService>();
            services.AddSingleton<IPetParentDeviceTokenService, InMemoryPetParentDeviceTokenService>();
        }
        else
        {
            services.AddScoped<IProviderOnboardingService>(provider =>
                new SqlProviderOnboardingService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>(),
                    provider.GetRequiredService<IProviderMobileOtpSender>()));

            services.AddScoped<IProviderServiceLocationRegistry>(provider =>
                new SqlProviderServiceLocationRegistry(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderPolicyService>(provider =>
                new SqlProviderPolicyService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderOnboardingStatusReader>(provider =>
                new SqlProviderOnboardingStatusReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IEventSqlStore>(provider =>
                new SqlEventStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IEventBookingSqlStore>(provider =>
                new SqlEventBookingStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderAvailabilityService>(provider =>
                new SqlProviderAvailabilityService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IBookingSqlStore>(provider =>
                new SqlBookingStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<INightStayBookingSqlStore>(provider =>
                new SqlNightStayBookingStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Only the standalone geolocation writes come through here — every fix
            // taken at a status transition is written by that transition's own
            // procedure, so it cannot be lost after the transition commits.
            services.AddScoped<IBookingLocationService>(provider =>
                new SqlBookingLocationService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // NOTE (2026-08-02): the BookingExpirySweeper hosted service used to
            // be registered here, running Booking.ExpireStaleBookings every 10
            // minutes in BOTH hosts. Time-driven booking settlement (stale
            // CREATED -> EXPIRED, expired parent modification requests,
            // unstarted-job no-shows) has moved out of the API hosts and out of
            // the database into a scheduled external job. Nothing in-process
            // settles bookings on a clock any more — do not re-add a sweeper
            // here without retiring that job first, or the two will race.

            services.AddScoped<IProviderClosureSqlStore>(provider =>
                new SqlProviderClosureStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderServiceCatalog>(provider =>
                new SqlProviderServiceCatalog(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IParentOnboardingService>(provider =>
                new SqlParentOnboardingService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>(),
                    provider.GetRequiredService<IPetParentMobileOtpSender>()));

            services.AddScoped<IParentPetService>(provider =>
                new SqlParentPetService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IPetNextConsultationStore>(provider =>
                new SqlPetNextConsultationStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IPetParentPhotoService>(provider =>
                new SqlPetParentPhotoService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderPhotoService>(provider =>
                new SqlProviderPhotoService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderServiceBannerService>(provider =>
                new SqlProviderServiceBannerService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderBannerImageService>(provider =>
                new SqlProviderBannerImageService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IPetParentOnboardingStatusReader>(provider =>
                new SqlPetParentOnboardingStatusReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IPetParentOwnershipReader>(provider =>
                new SqlPetParentOwnershipReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderBookingStatsReader>(provider =>
                new SqlProviderBookingStatsReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // The jobs behind a chat thread — every booking of either kind
            // between one provider and one pet parent.
            services.AddScoped<IParentProviderBookingReader>(provider =>
                new SqlParentProviderBookingReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderNameReader>(provider =>
                new SqlProviderNameReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Backs the discovery/search active filter — the provider's master
            // Active switch lives here in SQL, not in the Cosmos offering doc
            // that discovery lists from.
            services.AddScoped<IProviderActiveStatusReader>(provider =>
                new SqlProviderActiveStatusReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Earnings / spend reporting. Both sides read the shared
            // Booking.BookingAmounts function, so a provider's "earned" and a
            // parent's "spent" on the same booking are the same number by
            // construction.
            services.AddScoped<IProviderEarningsStore>(provider =>
                new SqlProviderEarningsStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IParentSpendStore>(provider =>
                new SqlParentSpendStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IProviderContactReader>(provider =>
                new SqlProviderContactReader(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Booking reviews. One store serves both interfaces — the provider's
            // received reviews and a parent's aggregate rating are the two directions
            // of the same table, so registering the concrete type once and mapping
            // both interfaces to it keeps them reading the same rows.
            services.AddScoped(provider =>
                new SqlBookingReviewStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));
            services.AddScoped<IBookingReviewStore>(sp => sp.GetRequiredService<SqlBookingReviewStore>());
            services.AddScoped<IPetParentRatingReader>(sp => sp.GetRequiredService<SqlBookingReviewStore>());

            // Support tickets. One store serves all three interfaces: the chat host's
            // legal-hold check and the "have I already reported this?" lookup each ask
            // Support.Tickets one narrow question, and giving either its own store would
            // only let them drift apart on what "open" means.
            services.AddScoped(provider =>
                new SqlSupportTicketStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));
            services.AddScoped<ISupportTicketStore>(sp => sp.GetRequiredService<SqlSupportTicketStore>());
            services.AddScoped<ISupportLegalHoldReader>(sp => sp.GetRequiredService<SqlSupportTicketStore>());
            services.AddScoped<IMySupportTicketLookup>(sp => sp.GetRequiredService<SqlSupportTicketStore>());

            // Blocking. One store serves the write side and the per-request
            // "who am I blocked from" lookup: they are two questions about one table,
            // and separating them would only duplicate the connection plumbing and let
            // them drift on what a block means.
            services.AddScoped(provider =>
                new SqlBlockStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));
            services.AddScoped<IBlockStore>(sp => sp.GetRequiredService<SqlBlockStore>());
            services.AddScoped<IMyBlockLookup>(sp => sp.GetRequiredService<SqlBlockStore>());

            // Push notifications are enqueued onto Notification.NotificationOutbox
            // here and dispatched to FCM by Pawfront.Functions — neither API host
            // talks to Firebase, which keeps the external call off the request
            // path and lets the SQL-only booking sweeps enqueue identically.
            services.AddScoped<INotificationPublisher>(provider =>
                new SqlNotificationPublisher(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>(),
                    provider.GetRequiredService<ILogger<SqlNotificationPublisher>>()));

            // FCM tokens rotate (reinstall, cleared data, restore), so each app
            // re-registers its current token independently of the sign-in flow.
            services.AddScoped<IProviderDeviceTokenService>(provider =>
                new SqlProviderDeviceTokenService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IPetParentDeviceTokenService>(provider =>
                new SqlPetParentDeviceTokenService(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Chat's SQL side: the thread index, per-side read state and blocks.
            // Message bodies are not here — they live in the Cosmos ChatMessages
            // container, registered by AddPawfrontCosmosInfrastructure.
            services.AddScoped<IChatConversationStore>(provider =>
                new SqlChatConversationStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            services.AddScoped<IChatPresenceStore>(provider =>
                new SqlChatPresenceStore(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>()));

            // Closes out a notification the chat host sent itself, through the
            // same procedure the scheduled dispatcher uses.
            services.AddScoped<IInstantNotificationSender>(provider =>
                new SqlInstantNotificationSender(
                    sqlConnectionString,
                    provider.GetService<IPawfrontSecretProvider>(),
                    provider.GetRequiredService<ILogger<SqlInstantNotificationSender>>()));
        }

        return services;
    }
}

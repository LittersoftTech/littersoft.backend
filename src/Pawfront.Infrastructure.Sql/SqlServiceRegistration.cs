using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
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
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Infrastructure.Sql.Availability;
using Pawfront.Infrastructure.Sql.Bookings;
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
            services.AddSingleton<IProviderServiceCatalog, InMemoryProviderServiceCatalog>();
            services.AddSingleton<IPetNextConsultationStore, InMemoryPetNextConsultationStore>();
            services.AddSingleton<IProviderNameReader, NullProviderNameReader>();
            services.AddSingleton<IProviderContactReader, NullProviderContactReader>();
            // Earnings are aggregates over the booking tables joined to the payment
            // ledger, neither of which the in-memory stores keep — report zeros
            // rather than 500ing the reporting screens.
            services.AddSingleton<IProviderEarningsStore, NullProviderEarningsStore>();
            services.AddSingleton<IParentSpendStore, NullParentSpendStore>();
            // No outbox table to write to — log and drop, same posture as the
            // booking sweeps having no in-memory equivalent.
            services.AddSingleton<INotificationPublisher, NullNotificationPublisher>();

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

            services.AddScoped<IProviderNameReader>(provider =>
                new SqlProviderNameReader(
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
        }

        return services;
    }
}

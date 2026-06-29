using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;
using Pawfront.Application.Events;
using Pawfront.Application.Onboarding;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Application.ParentPhotos;
using Pawfront.Application.Policies;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.ProviderPhotos;
using Pawfront.Application.ProviderServiceBanners;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Infrastructure.Sql.Availability;
using Pawfront.Infrastructure.Sql.Bookings;
using Pawfront.Infrastructure.Sql.Closures;
using Pawfront.Infrastructure.Sql.Events;
using Pawfront.Infrastructure.Sql.Onboarding;
using Pawfront.Infrastructure.Sql.ParentOnboarding;
using Pawfront.Infrastructure.Sql.ParentPets;
using Pawfront.Infrastructure.Sql.ParentPhotos;
using Pawfront.Infrastructure.Sql.Policies;
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
            services.AddSingleton<IBookingSqlStore, InMemoryBookingStore>();
            services.AddSingleton<INightStayBookingSqlStore, InMemoryNightStayBookingStore>();
            services.AddSingleton<IProviderClosureSqlStore, InMemoryProviderClosureStore>();
            services.AddSingleton<IProviderServiceCatalog, InMemoryProviderServiceCatalog>();
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
        }

        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.Events;
using Pawfront.Application.Onboarding;
using Pawfront.Application.Policies;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.Providers;
using Pawfront.Application.Services;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Infrastructure.Sql.Bookings;
using Pawfront.Infrastructure.Sql.Events;
using Pawfront.Infrastructure.Sql.Onboarding;
using Pawfront.Infrastructure.Sql.Policies;
using Pawfront.Infrastructure.Sql.ProviderOnboarding;
using Pawfront.Infrastructure.Sql.Providers;
using Pawfront.Infrastructure.Sql.ProviderServices;
using Pawfront.Infrastructure.Sql.Services;

namespace Pawfront.Infrastructure.Sql;

public static class SqlServiceRegistration
{
    public static IServiceCollection AddPawfrontSqlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IProviderService, InMemoryProviderService>();
        services.AddSingleton<IPetServiceCatalog, InMemoryPetServiceCatalog>();
        services.AddSingleton<IBookingService, InMemoryBookingService>();
        services.TryAddSingleton<IProviderMobileOtpSender, NoOpProviderMobileOtpSender>();

        var sqlConnectionString = configuration.GetConnectionString("SqlServer");
        var useKeyVault = configuration.GetValue("AzureKeyVault:Enabled", true);

        if (string.IsNullOrWhiteSpace(sqlConnectionString) && !useKeyVault)
        {
            services.AddSingleton<IProviderOnboardingService, InMemoryProviderOnboardingService>();
            services.AddSingleton<IProviderServiceLocationRegistry, InMemoryProviderServiceLocationRegistry>();
            services.AddSingleton<IProviderPolicyService, InMemoryProviderPolicyService>();
            services.AddSingleton<IProviderOnboardingStatusReader, InMemoryProviderOnboardingStatusReader>();
            services.AddSingleton<IEventSqlStore, InMemoryEventStore>();
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
        }

        return services;
    }
}

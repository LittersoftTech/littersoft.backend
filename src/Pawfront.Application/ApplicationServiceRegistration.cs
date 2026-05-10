using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Onboarding;

namespace Pawfront.Application;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddPawfrontApplication(this IServiceCollection services)
    {
        services.TryAddScoped<IProviderOnboardingStatusService, ProviderOnboardingStatusService>();
        return services;
    }
}

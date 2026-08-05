using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public static class FirebaseServiceRegistration
{
    /// <summary>
    /// Registers FCM sending, bound from the <c>Notifications</c> configuration
    /// section.
    ///
    /// Only <c>Pawfront.Functions</c> calls this. Neither API host references
    /// Firebase at all — that falls out of the outbox design, where the hosts only
    /// ever write a row and the dispatcher owns delivery.
    ///
    /// Both are singletons: <see cref="FirebaseAppRegistry"/> caches the two
    /// initialised Firebase apps, which are expensive to build and hold their own
    /// OAuth token cache.
    /// </summary>
    public static IServiceCollection AddPawfrontFirebaseMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));

        services.TryAddSingleton<FirebaseAppRegistry>();
        services.TryAddSingleton<IPushSender, FirebasePushSender>();

        return services;
    }
}

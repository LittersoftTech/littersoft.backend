using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public static class FirebaseServiceRegistration
{
    /// <summary>Inbox reads and persisted read-status updates for the mobile API hosts.</summary>
    public static IServiceCollection AddPawfrontFirestoreInbox(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.TryAddSingleton<FirebaseAppRegistry>();
        services.TryAddSingleton<INotificationInboxReader, FirestoreNotificationReader>();
        services.TryAddSingleton<INotificationReadStateStore, FirestoreNotificationReadStateStore>();
        return services;
    }

    /// <summary>
    /// Registers FCM sending, bound from the <c>Notifications</c> configuration
    /// section.
    ///
    /// Used by Pawfront.Functions and the ChatApi background worker. Request
    /// handlers only enqueue; background delivery sends FCM and writes Firestore.
    ///
    /// Singleton clients share each audience's Firebase app and credential cache.
    /// </summary>
    public static IServiceCollection AddPawfrontFirebaseMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));

        services.TryAddSingleton<FirebaseAppRegistry>();
        services.TryAddSingleton<FirebasePushSender>();
        services.TryAddSingleton<INotificationInboxWriter, FirestoreNotificationWriter>();
        services.TryAddSingleton<IPushSender>(provider => new InboxPushSender(
            provider.GetRequiredService<FirebasePushSender>(),
            provider.GetRequiredService<INotificationInboxWriter>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InboxPushSender>>()));

        return services;
    }
}

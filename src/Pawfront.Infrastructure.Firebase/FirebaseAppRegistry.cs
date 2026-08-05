using System.Collections.Concurrent;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

/// <summary>
/// Creates and caches one <see cref="FirebaseApp"/> per audience.
///
/// Two apps exist because the provider and pet-parent mobile apps are registered
/// in DIFFERENT Firebase projects. A device token issued by one project is
/// meaningless to the other — sending with the wrong credential fails with
/// SENDER_ID_MISMATCH — so the audience must select the credential.
///
/// <see cref="FirebaseApp.Create(AppOptions, string)"/> throws if the same name is
/// created twice, and loading credentials is async, so initialisation is
/// serialised through a cached <see cref="Lazy{T}"/> of a task: the factory runs
/// at most once per audience for the lifetime of the process.
/// </summary>
public sealed class FirebaseAppRegistry(
    IOptions<NotificationOptions> options,
    IPawfrontSecretProvider secretProvider)
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>
    /// Successfully initialised clients only. A FAILED initialisation is
    /// deliberately never cached: credentials arriving late (a Key Vault secret
    /// added after deploy) or a transient vault outage must be recoverable on the
    /// next tick, whereas caching the failure would silence that audience until
    /// the Function App recycled.
    /// </summary>
    private readonly ConcurrentDictionary<NotificationAudience, FirebaseMessaging> _messaging = new();

    /// <summary>Serialises initialisation so two ticks can't race to create the same app.</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// The messaging client for an audience. Throws
    /// <see cref="InvalidOperationException"/> when that audience has no
    /// credentials configured — a deployment mistake, and one worth failing
    /// loudly on rather than silently dropping every notification to half the
    /// userbase.
    /// </summary>
    public async Task<FirebaseMessaging> GetMessagingAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        if (_messaging.TryGetValue(audience, out var cached))
        {
            return cached;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have initialised it while we queued.
            if (_messaging.TryGetValue(audience, out cached))
            {
                return cached;
            }

            var messaging = await CreateMessagingAsync(audience, cancellationToken);
            _messaging[audience] = messaging;
            return messaging;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Whether an audience can be sent to at all. Lets the dispatcher report a
    /// clear configuration error per notification instead of throwing per batch.
    /// </summary>
    public bool IsConfigured(NotificationAudience audience) => GetProjectOptions(audience).IsConfigured;

    private async Task<FirebaseMessaging> CreateMessagingAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        var projectOptions = GetProjectOptions(audience);
        var appName = $"pawfront-{audience.ToSqlValue().ToLowerInvariant()}";

        // A process restart is the normal case, but guard against a name that a
        // previous registry instance in the same process already created.
        var existing = FirebaseApp.GetInstance(appName);
        if (existing is not null)
        {
            return FirebaseMessaging.GetMessaging(existing);
        }

        var credentialJson = await LoadCredentialJsonAsync(audience, projectOptions, cancellationToken);

        var app = FirebaseApp.Create(
            new AppOptions
            {
                Credential = GoogleCredential.FromJson(credentialJson),
                ProjectId = projectOptions.ProjectId
            },
            appName);

        return FirebaseMessaging.GetMessaging(app);
    }

    private async Task<string> LoadCredentialJsonAsync(
        NotificationAudience audience,
        FirebaseProjectOptions projectOptions,
        CancellationToken cancellationToken)
    {
        // File first: it is the local-development path, and a developer with a
        // file on disk should not need Key Vault access to send a test push.
        if (!string.IsNullOrWhiteSpace(projectOptions.CredentialsFilePath))
        {
            var path = ResolveCredentialPath(projectOptions.CredentialsFilePath);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Firebase service-account file for the {audience} app was not found at '{path}'.");
            }

            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(projectOptions.CredentialsSecretName))
        {
            return await secretProvider.GetSecretValueAsync(
                projectOptions.CredentialsSecretName, cancellationToken);
        }

        throw new InvalidOperationException(
            $"No Firebase credentials are configured for the {audience} app. Set either " +
            $"'Notifications:{audience}:CredentialsFilePath' or " +
            $"'Notifications:{audience}:CredentialsSecretName'.");
    }

    /// <summary>
    /// Resolves a relative <c>CredentialsFilePath</c> against the application's
    /// base directory rather than the process working directory.
    ///
    /// This matters: the Functions host does not guarantee the working directory
    /// is the published payload folder, so a path like
    /// <c>Secrets/provider-firebase.json</c> would resolve somewhere unpredictable
    /// — the same trap that made Program.cs load its JSON config from
    /// <see cref="AppContext.BaseDirectory"/>. Absolute paths are used as-is.
    /// </summary>
    private static string ResolveCredentialPath(string configuredPath)
    {
        var path = configuredPath.Trim();

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private FirebaseProjectOptions GetProjectOptions(NotificationAudience audience) => audience switch
    {
        NotificationAudience.Provider => _options.Provider,
        NotificationAudience.PetParent => _options.PetParent,
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown notification audience.")
    };
}

using System.Collections.Concurrent;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
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
/// serialised through a lock. Successful clients are reused per audience.
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
    private readonly ConcurrentDictionary<NotificationAudience, FirebaseApp> _apps = new();
    private readonly ConcurrentDictionary<NotificationAudience, FirestoreDb> _firestore = new();
    private readonly SemaphoreSlim _firestoreLock = new(1, 1);

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
        => FirebaseMessaging.GetMessaging(await GetAppAsync(audience, cancellationToken));

    public async Task<FirestoreDb> GetFirestoreAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        if (_firestore.TryGetValue(audience, out var cached)) return cached;
        await _firestoreLock.WaitAsync(cancellationToken);
        try
        {
            if (_firestore.TryGetValue(audience, out cached)) return cached;
            var app = await GetAppAsync(audience, cancellationToken);
            // Explicit credentials/project prevent either audience from falling back
            // to the host's default Google credentials or the other app's project.
            var projectId = app.Options.ProjectId
                ?? (app.Options.Credential.UnderlyingCredential as ServiceAccountCredential)?.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
                throw new InvalidOperationException($"No Firebase project ID is configured for {audience}.");
            var db = await new FirestoreDbBuilder
            {
                ProjectId = projectId,
                DatabaseId = GetProjectOptions(audience).FirestoreDatabaseId,
                GoogleCredential = app.Options.Credential
            }.BuildAsync(cancellationToken);
            _firestore[audience] = db;
            return db;
        }
        finally { _firestoreLock.Release(); }
    }

    private async Task<FirebaseApp> GetAppAsync(
        NotificationAudience audience,
        CancellationToken cancellationToken)
    {
        if (_apps.TryGetValue(audience, out var cached))
        {
            return cached;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have initialised it while we queued.
            if (_apps.TryGetValue(audience, out cached))
            {
                return cached;
            }

            var app = await CreateAppAsync(audience, cancellationToken);
            _apps[audience] = app;
            return app;
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

    private async Task<FirebaseApp> CreateAppAsync(
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
            return existing;
        }

        var credentialJson = await LoadCredentialJsonAsync(audience, projectOptions, cancellationToken);

        var app = FirebaseApp.Create(
            new AppOptions
            {
                Credential = CredentialFactory.FromJson<ServiceAccountCredential>(credentialJson).ToGoogleCredential(),
                ProjectId = projectOptions.ProjectId
            },
            appName);

        return app;
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

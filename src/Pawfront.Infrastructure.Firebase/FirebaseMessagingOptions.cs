namespace Pawfront.Infrastructure.Firebase;

/// <summary>
/// Credentials for ONE Firebase project. There are two of these because the
/// provider and pet-parent apps live in separate Firebase projects, and FCM
/// HTTP v1 is per-project: a message can only be sent with the credential of the
/// project that issued the device token.
/// </summary>
public sealed class FirebaseProjectOptions
{
    /// <summary>
    /// e.g. <c>littersoftprovider</c> / <c>pawfrontparent-89296</c>. Must match the
    /// <c>project_id</c> in the service-account JSON, and the <c>Firebase:ProjectId</c>
    /// the matching API host validates tokens against.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Path to the service-account JSON on disk. Intended for local development —
    /// keep the file OUT of source control. Takes precedence over
    /// <see cref="CredentialsSecretName"/> when both are set.
    /// </summary>
    public string? CredentialsFilePath { get; set; }

    /// <summary>
    /// Name of the secret holding the service-account JSON, resolved through
    /// <c>IPawfrontSecretProvider</c> — Key Vault in production, the
    /// <c>LocalSecrets:{name}</c> config section locally. The intended production
    /// path: the JSON contains a private key and must never sit in appsettings.
    /// </summary>
    public string? CredentialsSecretName { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(CredentialsFilePath) || !string.IsNullOrWhiteSpace(CredentialsSecretName);
}

/// <summary>
/// How a notification is presented on Android.
///
/// Note <see cref="Icon"/> is a DRAWABLE RESOURCE NAME bundled in the app
/// (e.g. <c>ic_notification</c>), not a URL — the server can only name a resource
/// the app already ships. Rich imagery travels as the message's image URL instead,
/// which must be publicly fetchable because the OS downloads it anonymously.
/// </summary>
public sealed class AndroidNotificationOptions
{
    /// <summary>
    /// Must match a channel the app has already created. Android 8+ silently
    /// DROPS a notification naming an unknown channel — the single most common
    /// cause of "the server says sent but nothing appears".
    /// </summary>
    public string ChannelId { get; set; } = "pawfront_default";

    public string Icon { get; set; } = "ic_notification";

    /// <summary>Accent colour applied to the small icon, as <c>#RRGGBB</c>.</summary>
    public string? Color { get; set; }
}

/// <summary>Dispatcher tuning — how much the timer job takes on per tick.</summary>
public sealed class NotificationDispatchOptions
{
    /// <summary>Notifications claimed per tick. The sproc clamps this to 500.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Delivery attempts before a notification is abandoned as Failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long a claim is held. Must comfortably exceed the time to send one
    /// batch, or a slow tick's rows get re-claimed while still in flight.
    /// </summary>
    public int LeaseMinutes { get; set; } = 5;
}

/// <summary>Bound from the <c>Notifications</c> configuration section.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Credentials for the provider app's Firebase project.</summary>
    public FirebaseProjectOptions Provider { get; set; } = new();

    /// <summary>Credentials for the pet-parent app's Firebase project.</summary>
    public FirebaseProjectOptions PetParent { get; set; } = new();

    public AndroidNotificationOptions Android { get; set; } = new();

    public NotificationDispatchOptions Dispatch { get; set; } = new();
}

namespace Pawfront.Contracts.DeviceTokens;

/// <summary>
/// Registers or refreshes the caller's FCM token. Sent by the app on every
/// launch and from Firebase's <c>onTokenRefresh</c> callback — the token is not
/// stable across reinstalls, cleared app data, or restores.
/// </summary>
/// <param name="FcmToken">The current FCM registration token.</param>
/// <param name="DeviceId">
/// A stable per-device identifier. Optional, but <b>strongly recommended</b>:
/// it is what lets the server retire the pre-reinstall token for the same
/// handset immediately, instead of pushing to a dead token until Firebase
/// reports it unregistered.
/// </param>
/// <param name="DevicePlatform">"Android" or "iOS". Case-insensitive.</param>
public sealed record RegisterDeviceTokenRequest(
    string FcmToken,
    string? DeviceId = null,
    string? DevicePlatform = null);

/// <summary>
/// Deactivates one of the caller's tokens. Sent on sign-out.
///
/// Posted to <c>/device-tokens/deactivate</c> rather than sent as a DELETE body:
/// the token must not travel in the URL (it would land in every access log on the
/// way), and DELETE bodies are dropped by some proxies — a sign-out that silently
/// failed would leave the handset receiving the previous account's notifications.
/// </summary>
public sealed record DeactivateDeviceTokenRequest(string FcmToken);

/// <summary>
/// The stored device-token row. The FCM token itself is deliberately NOT echoed
/// back — the client already has it, and there is no reason to widen where it
/// appears.
/// </summary>
/// <param name="OwnerId">
/// The caller's PetParentId / ProviderId. Null until they finish onboarding —
/// a token can be registered before the profile exists, and is back-filled by
/// the profile-completion flow.
/// </param>
/// <param name="RetiredTokenCount">
/// How many stale tokens for the same <c>DeviceId</c> were deactivated by this
/// call. Non-zero on the first launch after a reinstall; always 0 when no
/// <c>DeviceId</c> was supplied.
/// </param>
public sealed record DeviceTokenResponse(
    Guid DeviceTokenId,
    Guid? OwnerId,
    string? DeviceId,
    string? DevicePlatform,
    bool IsActive,
    int RetiredTokenCount,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

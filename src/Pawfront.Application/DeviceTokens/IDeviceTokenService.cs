using Pawfront.Contracts.DeviceTokens;

namespace Pawfront.Application.DeviceTokens;

/// <summary>
/// Registers, refreshes and deactivates a caller's FCM device tokens.
///
/// Two host-specific interfaces rather than one shared abstraction, because the
/// two apps store their tokens in different tables against different auth
/// identities — the same reason <c>IProviderOnboardingService</c> and
/// <c>IParentOnboardingService</c> are separate.
///
/// The caller is always identified by their Firebase uid from the JWT, never by
/// an id in the request, so a token can only ever be bound to the caller.
/// </summary>
public interface IProviderDeviceTokenService
{
    /// <summary>
    /// Upserts the token and reactivates it. Throws
    /// <see cref="DeviceTokenIdentityNotFoundException"/> when the Firebase user
    /// has no provider auth identity (they must sign in first), or
    /// <see cref="ArgumentException"/> for a blank token / unsupported platform.
    /// </summary>
    Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId,
        RegisterDeviceTokenRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates one of the caller's tokens (sign-out). Throws
    /// <see cref="DeviceTokenNotFoundException"/> when the token is unknown OR
    /// belongs to somebody else — the two are indistinguishable by design.
    /// </summary>
    Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId,
        string fcmToken,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IProviderDeviceTokenService"/>
public interface IPetParentDeviceTokenService
{
    /// <inheritdoc cref="IProviderDeviceTokenService.RegisterAsync"/>
    Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId,
        RegisterDeviceTokenRequest request,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IProviderDeviceTokenService.DeactivateAsync"/>
    Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId,
        string fcmToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// The Firebase user has no auth identity row yet — they have a valid token but
/// have never completed the <c>firebase-auth</c> handshake.
/// </summary>
public sealed class DeviceTokenIdentityNotFoundException(string firebaseUserId)
    : Exception($"No auth identity was found for Firebase user '{firebaseUserId}'. Call the firebase-auth endpoint first.");

/// <summary>The token is unknown, or is not the caller's. Deliberately one case.</summary>
public sealed class DeviceTokenNotFoundException()
    : Exception("The device token was not found for this account.");

/// <summary>
/// Normalises the client-supplied platform onto the values
/// <c>CK_*DeviceTokens_DevicePlatform</c> allows.
/// </summary>
public static class DevicePlatforms
{
    public const string Android = "Android";
    public const string Ios = "iOS";

    /// <summary>
    /// Accepts any casing ("android", "IOS", "iOs") and returns the canonical
    /// stored value. Blank/omitted returns null, which the column allows —
    /// the platform is only used to shape the payload, so it is not worth
    /// rejecting a registration over.
    /// </summary>
    public static string? Normalize(string? devicePlatform)
    {
        if (string.IsNullOrWhiteSpace(devicePlatform))
        {
            return null;
        }

        var trimmed = devicePlatform.Trim();

        if (string.Equals(trimmed, Android, StringComparison.OrdinalIgnoreCase))
        {
            return Android;
        }

        if (string.Equals(trimmed, Ios, StringComparison.OrdinalIgnoreCase))
        {
            return Ios;
        }

        throw new ArgumentException(
            $"devicePlatform '{devicePlatform}' is not supported. Use 'Android' or 'iOS'.",
            nameof(devicePlatform));
    }
}

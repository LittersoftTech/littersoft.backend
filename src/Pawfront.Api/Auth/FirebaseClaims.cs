using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Api.Auth;

internal static class FirebaseClaims
{
    public static string GetFirebaseUserId(ClaimsPrincipal user)
    {
        return Required(FindFirst(user, "user_id") ?? FindFirst(user, "sub"), "Firebase user id");
    }

    public static SaveProviderFirebaseAuthCommand BuildCommand(
        ClaimsPrincipal user,
        SaveProviderFirebaseAuthRequest request)
    {
        var firebaseUserId = GetFirebaseUserId(user);
        var email = Required(FindFirst(user, "email"), "Firebase email");
        var signInProvider = ResolveSignInProvider(user);

        return new SaveProviderFirebaseAuthCommand(
            FirebaseUserId: firebaseUserId,
            AuthProvider: MapAuthProvider(signInProvider),
            FirebaseProviderId: signInProvider,
            Email: email,
            IsEmailVerified: ParseBooleanClaim(FindFirst(user, "email_verified")),
            DisplayName: FindFirst(user, "name"),
            FirebasePhoneNumber: FindFirst(user, "phone_number"),
            PhotoUrl: FindFirst(user, "picture"),
            FirebaseTenantId: GetFirebaseValue(user, "tenant"),
            FcmToken: request.FcmToken,
            DeviceId: request.DeviceId,
            DevicePlatform: request.DevicePlatform);
    }

    private static string? ResolveSignInProvider(ClaimsPrincipal user)
    {
        var direct = GetFirebaseValue(user, "sign_in_provider");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var flattened = FindFirst(user, "firebase.sign_in_provider");
        if (!string.IsNullOrWhiteSpace(flattened))
        {
            return flattened;
        }

        return GetFirstFirebaseIdentityKey(user);
    }

    private static string? GetFirstFirebaseIdentityKey(ClaimsPrincipal user)
    {
        var firebaseClaim = user.FindFirst("firebase")?.Value;
        if (string.IsNullOrWhiteSpace(firebaseClaim))
        {
            return null;
        }

        using var document = JsonDocument.Parse(firebaseClaim);
        if (!document.RootElement.TryGetProperty("identities", out var identities)
            || identities.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in identities.EnumerateObject())
        {
            if (string.Equals(property.Name, "email", StringComparison.Ordinal))
            {
                continue;
            }

            return property.Name;
        }

        return "password";
    }

    private static string? FindFirst(ClaimsPrincipal user, string claimType)
    {
        return user.FindFirst(claimType)?.Value;
    }

    private static string? GetFirebaseValue(ClaimsPrincipal user, string propertyName)
    {
        var firebaseClaim = user.FindFirst("firebase")?.Value;
        if (string.IsNullOrWhiteSpace(firebaseClaim))
        {
            return null;
        }

        using var document = JsonDocument.Parse(firebaseClaim);
        return document.RootElement.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static string MapAuthProvider(string? firebaseProviderId)
    {
        return firebaseProviderId switch
        {
            "google.com" or "Google" => "Google",
            "apple.com" or "Apple" => "Apple",
            "password" or "EmailPassword" => "EmailPassword",
            null or "" => throw new ArgumentException(
                "Firebase sign-in provider could not be resolved from the ID token."),
            var unsupported => throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Firebase sign-in provider '{unsupported}' is not supported."))
        };
    }

    private static bool ParseBooleanClaim(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required in the Firebase ID token.");
        }

        return value.Trim();
    }
}

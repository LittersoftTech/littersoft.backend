using System.Security.Claims;
using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Auth;

/// <summary>
/// Reads the two things the chat host needs off a validated Firebase token: who
/// the caller is, and which app they signed in from.
///
/// Deliberately much smaller than the two CRUD hosts' <c>FirebaseClaims</c>: this
/// host never onboards anybody, so it needs no sign-in-provider mapping, e-mail,
/// or display name. A caller who has not completed onboarding cannot chat, and
/// the resolution in <see cref="CurrentChatParticipant"/> is what enforces that.
/// </summary>
internal static class ChatClaims
{
    public static string GetFirebaseUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirst("user_id")?.Value ?? user.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Firebase user id is required in the ID token. The ChatUser policy should have rejected this request.");
        }

        return value.Trim();
    }

    /// <summary>
    /// Which app the caller signed in from, from the claim stamped by whichever
    /// JwtBearer scheme validated the token. The <c>ChatUser</c> policy requires
    /// the claim, so its absence here means the endpoint was reached without
    /// authorisation — a wiring bug, not a client error.
    /// </summary>
    public static ChatParticipantType GetParticipantType(ClaimsPrincipal user)
    {
        var value = user.FindFirst(AuthServiceCollectionExtensions.AudienceClaimType)?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The '{AuthServiceCollectionExtensions.AudienceClaimType}' claim is missing. " +
                "Every chat route must require the ChatUser policy.");
        }

        return ChatParticipantTypes.FromSqlValue(value);
    }
}

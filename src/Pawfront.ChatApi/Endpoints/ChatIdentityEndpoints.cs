using Pawfront.Application.Chat;
using Pawfront.ChatApi.Auth;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Who the caller is, as this host sees them.
///
/// It exists because the chat host is the only one that accepts two Firebase
/// projects, and "did my token land on the right scheme, and resolve to the right
/// id?" is the question worth being able to answer in one call — both while
/// wiring the mobile clients and when diagnosing a chat the user says they cannot
/// open. Mirrors <c>/provider-onboarding/me</c> and <c>/parent-onboarding/me</c>.
/// </summary>
internal static class ChatIdentityEndpoints
{
    public static IEndpointRouteBuilder MapChatIdentityEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/me", async (
            HttpContext httpContext,
            ICurrentChatParticipant currentParticipant,
            CancellationToken cancellationToken) =>
        {
            // Read from the claims rather than the resolver, so a caller with no
            // profile still learns which app the server thinks they signed in
            // from — that is exactly the case worth diagnosing.
            var participantType = ChatClaims.GetParticipantType(httpContext.User);
            var firebaseUserId = ChatClaims.GetFirebaseUserId(httpContext.User);

            var participant = await currentParticipant.GetParticipantAsync(cancellationToken);

            return ApiResults.Ok(new ChatIdentityResponse(
                ParticipantType: participantType.ToSqlValue(),
                ParticipantId: participant?.Id,
                FirebaseUserId: firebaseUserId,
                CanChat: participant is not null,
                UserId: participant?.ToUserId()));
        });

        return builder;
    }
}

/// <param name="ParticipantType">Which app validated the token — <c>Provider</c> or <c>PetParent</c>.</param>
/// <param name="ParticipantId">
/// The caller's ProviderId or PetParentId. Null when onboarding never produced a
/// profile, which is the one case where a valid token still cannot chat.
/// </param>
/// <param name="CanChat">
/// Whether this caller can open or use a conversation at all. False means every
/// chat route will answer 403 <c>ChatProfileNotCompleted</c> until they finish
/// onboarding on their own app.
/// </param>
/// <param name="UserId">
/// The SignalR user id this caller's connections are addressed by
/// (<c>Provider:{guid}</c> / <c>PetParent:{guid}</c>). Null when
/// <paramref name="CanChat"/> is false. Diagnostic only — clients never send it.
/// </param>
internal sealed record ChatIdentityResponse(
    string ParticipantType,
    Guid? ParticipantId,
    string FirebaseUserId,
    bool CanChat,
    string? UserId);

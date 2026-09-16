using System.Security.Claims;
using Pawfront.Application.Chat;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ProviderOnboarding;

namespace Pawfront.ChatApi.Auth;

/// <summary>
/// Maps a validated Firebase principal to a <see cref="ChatParticipant"/>.
///
/// Stateless and context-free on purpose. REST handlers reach it through the
/// per-request <see cref="ICurrentChatParticipant"/>, while the hub calls it
/// directly with <c>Hub.Context.User</c> — hub method invocations do not run
/// inside an HTTP request, so an <c>IHttpContextAccessor</c>-based service would
/// resolve to nothing there.
/// </summary>
internal interface IChatParticipantResolver
{
    Task<ChatParticipant?> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

internal sealed class ChatParticipantResolver(
    IProviderOnboardingService providerOnboardingService,
    IPetParentOwnershipReader petParentOwnershipReader,
    ILogger<ChatParticipantResolver> logger) : IChatParticipantResolver
{
    public async Task<ChatParticipant?> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var participantType = ChatClaims.GetParticipantType(user);
        var firebaseUserId = ChatClaims.GetFirebaseUserId(user);

        // Both branches reuse the resolver the owning host already uses, so a
        // token resolves to exactly the same id here as it does there.
        var participantId = participantType switch
        {
            ChatParticipantType.Provider =>
                await ResolveProviderIdAsync(firebaseUserId, cancellationToken),
            ChatParticipantType.PetParent =>
                await petParentOwnershipReader.GetPetParentIdByFirebaseUserIdAsync(
                    firebaseUserId, cancellationToken),
            _ => null
        };

        if (participantId is not { } id)
        {
            logger.LogDebug(
                "Firebase user resolved to no {ParticipantType} profile; the caller cannot chat yet.",
                participantType);
            return null;
        }

        return new ChatParticipant(participantType, id);
    }

    private async Task<Guid?> ResolveProviderIdAsync(
        string firebaseUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = await providerOnboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);

            // Null ProviderId means the auth identity exists but the profile was
            // never completed — the same "cannot act yet" state the pet-parent
            // reader signals by returning null, so both sides collapse to null.
            return provider.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            // The provider host throws where the parent host returns null. Absent
            // is absent either way; normalising here keeps the caller branch-free.
            return null;
        }
    }
}

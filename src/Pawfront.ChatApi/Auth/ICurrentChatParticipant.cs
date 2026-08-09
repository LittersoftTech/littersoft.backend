using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Auth;

/// <summary>
/// Resolves the caller's chat identity — which side of a conversation they are,
/// and their <c>ProviderId</c> or <c>PetParentId</c> — from their Firebase JWT,
/// caching the result for the lifetime of the request or hub connection.
///
/// This is the chat host's equivalent of the pet-parent host's
/// <c>ICurrentPetParentContext</c>, and it exists for the same reason: the
/// participant id is derived from the token, <b>never</b> from a route or body,
/// so a caller cannot act as somebody else by supplying their id.
/// </summary>
internal interface ICurrentChatParticipant
{
    /// <summary>
    /// The caller's chat identity, or <c>null</c> when they have no usable one:
    /// <list type="bullet">
    ///   <item>no auth identity row exists for this Firebase user, or</item>
    ///   <item>the auth identity exists but onboarding never produced a profile,
    ///         so there is no ProviderId / PetParentId to be.</item>
    /// </list>
    /// Callers translate <c>null</c> into 403 <c>ChatProfileNotCompleted</c>,
    /// mirroring the pet-parent host's <c>ParentProfileNotCompleted</c>.
    /// </summary>
    Task<ChatParticipant?> GetParticipantAsync(CancellationToken cancellationToken);
}

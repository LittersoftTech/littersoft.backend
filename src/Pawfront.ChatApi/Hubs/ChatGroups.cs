using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Hubs;

/// <summary>
/// The two group namespaces the hub addresses.
///
/// <b>Why groups and not <c>Clients.User(...)</c>.</b> SignalR's
/// <c>IUserIdProvider.GetUserId</c> is synchronous, but mapping a Firebase token
/// to a ProviderId / PetParentId needs an async SQL lookup — so the id this
/// product addresses people by simply cannot be produced there. Firebase uid
/// would be available, but the sender knows its counterparty's ProviderId, not
/// their uid, so it could never name them. A per-person group joined during
/// <c>OnConnectedAsync</c> (which IS async) solves both, and Azure SignalR routes
/// groups across instances for free.
/// </summary>
internal static class ChatGroups
{
    /// <summary>
    /// Everyone currently looking at one thread. Joined only after the caller is
    /// authorised against the conversation — a client-supplied group name is never
    /// trusted.
    /// </summary>
    public static string Conversation(Guid conversationId) => $"conv:{conversationId}";

    /// <summary>
    /// Every device one person has connected. This is what makes an inbox badge
    /// update on a phone that is not looking at the thread.
    /// </summary>
    public static string User(ChatParticipant participant) =>
        $"user:{participant.Type.ToSqlValue()}:{participant.Id}";
}

/// <summary>
/// The names of the server-to-client methods. Constants rather than inline
/// strings because they are a wire contract with two mobile apps: renaming one
/// silently stops delivery rather than failing a build.
/// </summary>
internal static class ChatHubEvents
{
    /// <summary>A new message on a thread the client has joined.</summary>
    public const string MessageReceived = "MessageReceived";

    /// <summary>The counterparty has read up to a sequence.</summary>
    public const string MessageRead = "MessageRead";

    /// <summary>The counterparty started or stopped typing. Never persisted.</summary>
    public const string TypingChanged = "TypingChanged";

    /// <summary>
    /// An inbox card changed. Sent to the recipient's own group, so a client
    /// elsewhere in the app still updates its list without joining the thread.
    /// </summary>
    public const string ConversationUpdated = "ConversationUpdated";

    /// <summary>The recipient's unread total for one thread.</summary>
    public const string UnreadCountChanged = "UnreadCountChanged";
}

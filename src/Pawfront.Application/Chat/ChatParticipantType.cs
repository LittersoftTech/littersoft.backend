using Pawfront.Application.Notifications;

namespace Pawfront.Application.Chat;

/// <summary>
/// Which side of a conversation somebody is. A thread always has exactly two
/// participants, one of each.
///
/// This deliberately mirrors <see cref="NotificationAudience"/> without being it:
/// the two enums agree today because a chat participant is also a push recipient,
/// but they answer different questions — this one says "whose row is this in
/// <c>Chat.ConversationParticipants</c>", the other says "which Firebase project
/// do we send with". Keeping them apart is what stops the chat schema from
/// depending on how notifications happen to be delivered.
/// </summary>
public enum ChatParticipantType
{
    /// <summary>A service provider, identified by their <c>ProviderId</c>.</summary>
    Provider,

    /// <summary>A pet parent, identified by their <c>PetParentId</c>.</summary>
    PetParent
}

/// <summary>
/// One end of a conversation: which side, and which id on that side.
/// </summary>
public readonly record struct ChatParticipant(ChatParticipantType Type, Guid Id)
{
    /// <summary>
    /// The stable key used as the SignalR user id, so <c>Clients.User(...)</c>
    /// reaches a person across every device they have open. The type prefix
    /// matters: provider and parent ids come from different tables and are only
    /// unique within their own, so the raw GUID alone is not a safe key.
    /// </summary>
    public string ToUserId() => $"{Type.ToSqlValue()}:{Id}";
}

public static class ChatParticipantTypes
{
    /// <summary>
    /// The literal stored in <c>Chat.ConversationParticipants.ParticipantType</c>
    /// and checked by its CHECK constraint. Explicit rather than
    /// <see cref="Enum.ToString()"/> so renaming the enum member can never
    /// silently change what is written to the database — same reasoning as
    /// <see cref="NotificationAudiences.ToSqlValue"/>.
    /// </summary>
    public static string ToSqlValue(this ChatParticipantType type) => type switch
    {
        ChatParticipantType.Provider => "Provider",
        ChatParticipantType.PetParent => "PetParent",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown chat participant type.")
    };

    public static ChatParticipantType FromSqlValue(string value) => value switch
    {
        "Provider" => ChatParticipantType.Provider,
        "PetParent" => ChatParticipantType.PetParent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown chat participant type.")
    };

    /// <summary>
    /// The Firebase project / device-token table to notify this participant
    /// through.
    /// </summary>
    public static NotificationAudience ToNotificationAudience(this ChatParticipantType type) => type switch
    {
        ChatParticipantType.Provider => NotificationAudience.Provider,
        ChatParticipantType.PetParent => NotificationAudience.PetParent,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown chat participant type.")
    };

    /// <summary>The other side of the conversation.</summary>
    public static ChatParticipantType Counterparty(this ChatParticipantType type) => type switch
    {
        ChatParticipantType.Provider => ChatParticipantType.PetParent,
        ChatParticipantType.PetParent => ChatParticipantType.Provider,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown chat participant type.")
    };
}

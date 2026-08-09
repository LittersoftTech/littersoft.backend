namespace Pawfront.Application.Chat;

/// <summary>
/// Typed failures for the chat feature, mapped to HTTP by the endpoint layer —
/// the same convention the booking and review features follow.
/// </summary>
public sealed class ConversationNotFoundException(Guid conversationId)
    : Exception($"Conversation '{conversationId}' was not found.")
{
    public Guid ConversationId { get; } = conversationId;
}

/// <summary>
/// The counterparty could not be found, or their account has been deleted.
/// Deliberately one exception for both on the parent side: a deleted parent's
/// Firebase identity is severed so they could never read the thread, and telling
/// the provider which it was leaks whether the account ever existed.
/// </summary>
public sealed class ChatCounterpartyNotFoundException(ChatParticipantType type, Guid id)
    : Exception($"{type} '{id}' was not found.")
{
    public ChatParticipantType CounterpartyType { get; } = type;
    public Guid CounterpartyId { get; } = id;
}

/// <summary>The provider's account has been deleted, so no new thread can open with them.</summary>
public sealed class ChatProviderAccountDeletedException(Guid providerId)
    : Exception($"Provider '{providerId}' has deleted their account.")
{
    public Guid ProviderId { get; } = providerId;
}

/// <summary>
/// One party has blocked the other. The message is deliberately neutral and the
/// direction is not disclosed — saying which way round it runs would confirm the
/// other person acted, which is the thing a block is meant to end.
/// </summary>
public sealed class ChatBlockedException()
    : Exception("This conversation is not available.");

/// <summary>The caller is not one of the thread's two participants.</summary>
public sealed class ChatForbiddenException(Guid conversationId)
    : Exception($"You are not a party to conversation '{conversationId}'.")
{
    public Guid ConversationId { get; } = conversationId;
}

/// <summary>A block between two participants on the same side, which can never apply.</summary>
public sealed class ChatInvalidBlockException()
    : Exception("A chat block must run between a provider and a pet parent.");

using Microsoft.AspNetCore.SignalR;
using Pawfront.Application.Chat;
using Pawfront.ChatApi.Endpoints;

namespace Pawfront.ChatApi.Hubs;

/// <summary>
/// The real implementation of <see cref="IChatRealtimePublisher"/>, replacing the
/// no-op Application registers by default.
///
/// It sends through <see cref="IHubContext{THub}"/> rather than from inside the
/// hub, because most fan-outs are triggered by <see cref="IChatService"/> — which
/// is also reached over REST, where there is no hub instance at all.
/// </summary>
internal sealed class SignalRChatRealtimePublisher(
    IHubContext<ChatHub> hubContext,
    ILogger<SignalRChatRealtimePublisher> logger) : IChatRealtimePublisher
{
    public async Task PublishMessageAsync(
        ChatMessage message,
        ChatParticipant recipient,
        int recipientUnreadCount,
        CancellationToken cancellationToken)
    {
        var payload = ChatMapping.ToResponse(message);

        // Two fan-outs, because they answer different questions.
        //
        // The thread group is everyone with this conversation OPEN — including the
        // sender's own other devices, which is what keeps a phone and a tablet in
        // step.
        await hubContext.Clients
            .Group(ChatGroups.Conversation(message.ConversationId))
            .SendAsync(ChatHubEvents.MessageReceived, payload, cancellationToken);

        // The recipient's personal group is every device they have connected,
        // whether or not they are on this thread. Without it, someone sitting on
        // their bookings screen would see no badge until they navigated to chat.
        await hubContext.Clients
            .Group(ChatGroups.User(recipient))
            .SendAsync(
                ChatHubEvents.ConversationUpdated,
                new
                {
                    conversationId = message.ConversationId,
                    lastSequence = message.Sequence,
                    lastMessageAtUtc = message.CreatedAtUtc,
                    unreadCount = recipientUnreadCount
                },
                cancellationToken);

        await hubContext.Clients
            .Group(ChatGroups.User(recipient))
            .SendAsync(
                ChatHubEvents.UnreadCountChanged,
                new { conversationId = message.ConversationId, unreadCount = recipientUnreadCount },
                cancellationToken);

        logger.LogDebug(
            "Published message {MessageId} to conversation {ConversationId} and to {RecipientType} {RecipientId}.",
            message.MessageId, message.ConversationId, recipient.Type, recipient.Id);
    }

    public async Task PublishMessageDeletedAsync(
        ChatMessage message,
        ChatParticipant counterparty,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            conversationId = message.ConversationId,
            messageId = message.MessageId,
            sequence = message.Sequence,
            deletedAtUtc = message.DeletedAtUtc
        };

        // Same two fan-outs as a new message, for the same two reasons.
        //
        // The thread group is everyone with the conversation OPEN — the recipient
        // staring at the message that has just been retracted, and the sender's
        // own other devices.
        await hubContext.Clients
            .Group(ChatGroups.Conversation(message.ConversationId))
            .SendAsync(ChatHubEvents.MessageDeleted, payload, cancellationToken);

        // The counterparty's personal group covers the case that matters almost
        // as much: they are somewhere else in the app with the inbox on screen,
        // and its preview line may have just become "This message was deleted".
        await hubContext.Clients
            .Group(ChatGroups.User(counterparty))
            .SendAsync(ChatHubEvents.MessageDeleted, payload, cancellationToken);

        logger.LogDebug(
            "Published the retraction of message {MessageId} on conversation {ConversationId} to " +
            "{CounterpartyType} {CounterpartyId}.",
            message.MessageId, message.ConversationId, counterparty.Type, counterparty.Id);
    }

    public Task PublishReadAsync(
        Guid conversationId,
        ChatParticipant reader,
        long lastReadSequence,
        ChatParticipant counterparty,
        CancellationToken cancellationToken)
    {
        // To the thread, not to the counterparty's personal group: a read receipt
        // is only meaningful to somebody looking at the conversation.
        return hubContext.Clients
            .Group(ChatGroups.Conversation(conversationId))
            .SendAsync(
                ChatHubEvents.MessageRead,
                new
                {
                    conversationId,
                    participantType = reader.Type.ToSqlValue(),
                    participantId = reader.Id,
                    lastReadSequence
                },
                cancellationToken);
    }

    public Task PublishTypingAsync(
        Guid conversationId,
        ChatParticipant typist,
        bool isTyping,
        ChatParticipant counterparty,
        CancellationToken cancellationToken)
    {
        // The hub broadcasts typing directly (it can exclude the typist's own
        // connection, which this cannot). This path exists for completeness so a
        // non-hub caller is not silently a no-op; clients ignore their own id.
        return hubContext.Clients
            .Group(ChatGroups.Conversation(conversationId))
            .SendAsync(
                ChatHubEvents.TypingChanged,
                new
                {
                    conversationId,
                    participantType = typist.Type.ToSqlValue(),
                    participantId = typist.Id,
                    isTyping
                },
                cancellationToken);
    }
}

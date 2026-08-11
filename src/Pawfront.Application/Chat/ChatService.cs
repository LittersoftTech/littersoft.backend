using Microsoft.Extensions.Logging;

namespace Pawfront.Application.Chat;

/// <summary>
/// Composes the two stores and the live fan-out into the chat use cases. See
/// <see cref="IChatService"/> for why both the REST endpoints and the hub come
/// through here.
/// </summary>
public sealed class ChatService(
    IChatConversationStore conversationStore,
    IChatMessageStore messageStore,
    IChatRealtimePublisher realtimePublisher,
    IChatPushDispatcher pushDispatcher,
    ILogger<ChatService> logger) : IChatService
{
    public Task<ChatConversationDetail> OpenConversationAsync(
        ChatParticipant actor,
        ChatParticipantType counterpartyType,
        Guid counterpartyId,
        CancellationToken cancellationToken)
    {
        if (counterpartyType == actor.Type)
        {
            // A thread always runs provider <-> parent. Same-side is not a
            // conversation that could ever exist, so it is rejected as a bad
            // request rather than looked up and 404'd.
            throw new ChatInvalidBlockException();
        }

        var (providerId, petParentId) = actor.Type == ChatParticipantType.Provider
            ? (actor.Id, counterpartyId)
            : (counterpartyId, actor.Id);

        return conversationStore.GetOrCreateAsync(providerId, petParentId, actor, cancellationToken);
    }

    public async Task<ChatConversationDetail> GetConversationAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        return await conversationStore.GetForParticipantAsync(conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);
    }

    public Task<IReadOnlyList<ChatConversationCard>> ListConversationsAsync(
        ChatParticipant participant,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        return conversationStore.ListAsync(
            participant,
            skip < 0 ? 0 : skip,
            Math.Clamp(take, 1, ChatLimits.MaxConversationPageSize),
            cancellationToken);
    }

    public async Task<ChatSendResult> SendMessageAsync(
        SendChatMessageCommand command,
        CancellationToken cancellationToken)
    {
        var text = NormalizeText(command.Text);
        ValidateBody(command.Kind, text, command.Attachment);

        // ---- Phase 1: reserve ------------------------------------------------
        // Authorises the sender and takes the sequence, and does NOTHING a client
        // can observe — no preview, no unread count, no push. Those are phase 2,
        // which runs only once the body is durable, so a send that fails at the
        // Cosmos write leaves the thread exactly as it was.
        //
        // This is also where a duplicate is caught. (ConversationId, MessageId) is
        // a primary key, so the same client id arriving twice — over the hub, over
        // REST, or one of each — reuses the ORIGINAL sequence instead of taking a
        // second one. It costs no extra round trip overall: this replaces the
        // Cosmos point read that used to do the same job less reliably, since that
        // read could not help in the one case that matters, where the Cosmos write
        // is what failed.
        var reservation = await conversationStore.ReserveMessageAsync(
            command.ConversationId, command.Sender, command.MessageId, cancellationToken);

        if (reservation.IsCommitted)
        {
            var replayed = await messageStore.TryGetAsync(
                command.ConversationId, command.MessageId, cancellationToken);

            if (replayed is not null)
            {
                logger.LogDebug(
                    "Message {MessageId} on conversation {ConversationId} was already sent; returning it unchanged.",
                    command.MessageId, command.ConversationId);

                // No delivery envelope: the original send already did that, and
                // repeating it would push the recipient twice for one message.
                return new ChatSendResult(replayed, NoFurtherDelivery(replayed));
            }

            // Committed with no body: a send torn apart before the two-phase write
            // existed. Fall through and write the body under its original sequence
            // rather than leaving the thread with a message nobody can read.
            logger.LogWarning(
                "Message {MessageId} on conversation {ConversationId} is committed but has no stored body; rewriting it.",
                command.MessageId, command.ConversationId);
        }

        var message = new ChatMessage(
            MessageId: command.MessageId,
            ConversationId: command.ConversationId,
            Sequence: reservation.Sequence,
            SenderType: command.Sender.Type,
            SenderId: command.Sender.Id,
            Kind: command.Kind,
            Text: text,
            Attachment: command.Attachment,
            CreatedAtUtc: reservation.CreatedAtUtc,
            EditedAtUtc: null,
            DeletedAtUtc: null);

        ChatMessage stored;
        try
        {
            stored = await messageStore.CreateAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            // Nothing observable was ever written, so undoing the send is just
            // freeing the reservation. Best-effort: if this fails too, a retry of
            // the same id finds the reservation uncommitted, reuses its sequence
            // and completes — which is the right outcome anyway.
            await ReleaseReservationAsync(command, exception, cancellationToken);
            throw;
        }

        // ---- Phase 2: commit -------------------------------------------------
        // The body is durable, so the message may now become visible: inbox cache,
        // read state, and the recipient's push. Idempotent, which is what lets a
        // caller retry a send that died here — the retry finds the body already in
        // Cosmos, comes straight back to this call, and finishes the delivery that
        // was missed.
        var append = await conversationStore.CommitMessageAsync(
            command.ConversationId,
            command.MessageId,
            BuildPreview(command.Kind, text),
            cancellationToken);

        // Best-effort, and deliberately after the message is durable. A socket
        // fan-out must never fail a send that is already stored — the client
        // reconciles on reconnect by re-reading from its last known sequence.
        try
        {
            await realtimePublisher.PublishMessageAsync(
                stored,
                new ChatParticipant(append.RecipientType, append.RecipientId),
                append.RecipientUnreadCount,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Realtime fan-out failed for message {MessageId}; it is stored and will be picked up on reconnect.",
                stored.MessageId);
        }

        // Queue the push, if the recipient earned one. The row is already written
        // and leased by Chat.CommitMessageAppend, so this only decides whether it
        // goes out in a second or in a minute — never whether it goes out at all.
        if (append.NotificationId is { } notificationId && append.RecipientTokens.Count > 0)
        {
            pushDispatcher.Enqueue(new ChatPushWorkItem(
                notificationId,
                append.RecipientType.ToNotificationAudience(),
                append.RecipientId,
                append.ConversationId,
                append.NotificationDataJson,
                append.RecipientTokens));
        }

        return new ChatSendResult(stored, append);
    }

    public async Task<ChatMessagePage> GetHistoryAsync(
        Guid conversationId,
        ChatParticipant participant,
        long? beforeSequence,
        int take,
        CancellationToken cancellationToken)
    {
        // Authorise against SQL before touching Cosmos. The message store has no
        // idea who may read a thread — partitioning by conversation makes the
        // query cheap, not safe.
        _ = await conversationStore.GetForParticipantAsync(conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        return await messageStore.ListAsync(
            conversationId,
            beforeSequence,
            Math.Clamp(take, 1, ChatLimits.MaxPageSize),
            cancellationToken);
    }

    public async Task<ChatParticipantState> MarkReadAsync(
        Guid conversationId,
        ChatParticipant participant,
        long upToSequence,
        CancellationToken cancellationToken)
    {
        var state = await conversationStore.MarkReadAsync(
            conversationId, participant, upToSequence, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        // Let the counterparty's read receipts update. Best-effort for the same
        // reason as the message fan-out.
        try
        {
            var conversation = await conversationStore.GetForParticipantAsync(
                conversationId, participant, cancellationToken);

            if (conversation is not null)
            {
                await realtimePublisher.PublishReadAsync(
                    conversationId,
                    participant,
                    state.LastReadSequence,
                    new ChatParticipant(
                        conversation.Counterparty.ParticipantType,
                        conversation.Counterparty.ParticipantId),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to publish a read receipt for conversation {ConversationId}.",
                conversationId);
        }

        return state;
    }

    public Task<ChatUnreadSummary> GetUnreadSummaryAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        return conversationStore.GetUnreadSummaryAsync(participant, cancellationToken);
    }

    public async Task<ChatMessage> DeleteMessageAsync(
        Guid conversationId,
        Guid messageId,
        ChatParticipant sender,
        CancellationToken cancellationToken)
    {
        _ = await conversationStore.GetForParticipantAsync(conversationId, sender, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        // Scoped to the sender inside the store, so "not yours" and "no such
        // message" come back as the same null and cannot be told apart.
        return await messageStore.SoftDeleteAsync(conversationId, messageId, sender, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);
    }

    public Task<ChatBlock> BlockAsync(
        ChatParticipant blocker,
        ChatParticipantType blockedType,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken)
    {
        return conversationStore.BlockAsync(blocker, blockedType, blockedId, reason, cancellationToken);
    }

    public Task<ChatBlock?> UnblockAsync(
        Guid chatBlockId,
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        return conversationStore.UnblockAsync(chatBlockId, blocker, cancellationToken);
    }

    public Task<IReadOnlyList<ChatBlock>> ListBlocksAsync(
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        return conversationStore.ListBlocksAsync(blocker, cancellationToken);
    }

    /// <summary>
    /// Rolls back phase one after the body failed to write. Never throws: the
    /// caller is already failing the send with the real cause, and replacing that
    /// with a cleanup error would hide why the message did not go.
    /// </summary>
    private async Task ReleaseReservationAsync(
        SendChatMessageCommand command,
        Exception cause,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            cause,
            "Storing the body of message {MessageId} on conversation {ConversationId} failed; releasing its reservation so the send leaves no trace.",
            command.MessageId, command.ConversationId);

        try
        {
            await conversationStore.ReleaseMessageReservationAsync(
                command.ConversationId, command.MessageId, cancellationToken);
        }
        catch (Exception releaseException)
        {
            logger.LogError(
                releaseException,
                "Could not release the reservation for message {MessageId} on conversation {ConversationId}. Harmless: a retry of the same id reuses its sequence and completes the send.",
                command.MessageId, command.ConversationId);
        }
    }

    private static string? NormalizeText(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void ValidateBody(ChatMessageKind kind, string? text, ChatAttachment? attachment)
    {
        if (text is { Length: > ChatLimits.MaxTextLength })
        {
            throw new ArgumentException(
                $"A message may be at most {ChatLimits.MaxTextLength} characters.");
        }

        switch (kind)
        {
            case ChatMessageKind.Text when text is null:
                throw new ArgumentException("A text message needs some text.");

            case ChatMessageKind.Text when attachment is not null:
                throw new ArgumentException(
                    "A text message cannot carry an attachment. Send it as kind 'Image'.");

            case ChatMessageKind.Image when attachment is null:
                throw new ArgumentException(
                    "An image message needs an attachment. Upload it first, then send its url.");
        }
    }

    /// <summary>
    /// The one-line inbox summary. An image with a caption previews as its
    /// caption — that is what the sender actually said — and only a bare image
    /// falls back to the label.
    /// </summary>
    private static string BuildPreview(ChatMessageKind kind, string? text)
    {
        var source = text ?? (kind == ChatMessageKind.Image ? ChatLimits.ImagePreviewLabel : string.Empty);

        return source.Length <= ChatLimits.PreviewLength
            ? source
            : source[..ChatLimits.PreviewLength];
    }

    /// <summary>
    /// The delivery envelope for a replayed send: names the counterparty so the
    /// caller can still address the socket, but queues no notification.
    /// </summary>
    private static ChatAppendResult NoFurtherDelivery(ChatMessage message) =>
        new(
            ConversationId: message.ConversationId,
            MessageId: message.MessageId,
            Sequence: message.Sequence,
            CreatedAtUtc: message.CreatedAtUtc,
            RecipientType: message.SenderType.Counterparty(),
            RecipientId: Guid.Empty,
            RecipientUnreadCount: 0,
            RecipientIsViewing: false,
            RecipientIsMuted: false,
            NotificationId: null,
            RecipientTokens: [],
            NotificationDataJson: null);
}

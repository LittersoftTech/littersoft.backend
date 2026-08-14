using Microsoft.Extensions.Logging;
using Pawfront.Application.Bookings;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.Providers;

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
    // Only for the counterparty avatar — a provider's image lives in Cosmos, so
    // SQL cannot return it with the thread. See WithProviderPhotoAsync.
    IProviderDiscoveryService providerDiscovery,
    // The thread's "View Jobs" list. Chat owns the question ("what have we two
    // done together?"); the booking tables own the answer.
    IParentProviderBookingReader parentProviderBookings,
    IPendingJobEnricher pendingJobEnricher,
    // The legal hold on a reported thread. Only the MESSAGE delete needs it here:
    // clearing a thread is a T-SQL write and Chat.DeleteConversationForParticipant
    // checks the hold inline (THROW 51352), but a retraction is a Cosmos document
    // replace with no SQL statement in its path to hang the check on.
    Support.ISupportLegalHoldReader legalHoldReader,
    ILogger<ChatService> logger) : IChatService
{
    public async Task<ChatConversationDetail> OpenConversationAsync(
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

        var detail = await conversationStore.GetOrCreateAsync(
            providerId, petParentId, actor, cancellationToken);

        return detail with
        {
            Counterparty = await WithProviderPhotoAsync(detail.Counterparty, cancellationToken)
        };
    }

    public async Task<ChatConversationDetail> GetConversationAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        var detail = await conversationStore.GetForParticipantAsync(conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        return detail with
        {
            Counterparty = await WithProviderPhotoAsync(detail.Counterparty, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<ChatConversationCard>> ListConversationsAsync(
        ChatParticipant participant,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var cards = await conversationStore.ListAsync(
            participant,
            NormalizeSearch(search),
            skip < 0 ? 0 : skip,
            Math.Clamp(take, 1, ChatLimits.MaxConversationPageSize),
            cancellationToken);

        return await WithProviderPhotosAsync(cards, cancellationToken);
    }

    /// <summary>
    /// Blank is the same as absent — an empty search box must return the whole
    /// inbox, not nothing — and an over-long term is truncated to the column's
    /// width rather than rejected, since a search box is not a place to fail a
    /// request over length.
    /// </summary>
    private static string? NormalizeSearch(string? search)
    {
        var trimmed = search?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= ChatLimits.MaxSearchLength
            ? trimmed
            : trimmed[..ChatLimits.MaxSearchLength];
    }

    public async Task<ChatParticipantState> DeleteConversationAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        // The store scopes the write to the caller's own participant row, so
        // "no such thread" and "not yours" arrive as the same null — the
        // non-disclosure every other chat read holds.
        return await conversationStore.DeleteForParticipantAsync(
            conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);
    }

    public async Task<ChatConversationJobs> GetConversationJobsAsync(
        Guid conversationId,
        ChatParticipant participant,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Authorise first, and take the pair FROM the thread. The caller never
        // names a provider or a parent — the conversation id is the only handle,
        // and it already encodes both, so there is nothing extra to re-authorise.
        var detail = await conversationStore.GetForParticipantAsync(
            conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        var normalizedSkip = skip < 0 ? 0 : skip;
        var normalizedTake = Math.Clamp(take, 1, ChatLimits.MaxJobsPageSize);

        var page = await parentProviderBookings.ListAsync(
            detail.Conversation.ProviderId,
            detail.Conversation.PetParentId,
            normalizedSkip,
            normalizedTake,
            cancellationToken);

        // The provider photo and the money block cannot come from SQL — one lives
        // in Cosmos, the other needs the fee percentage and a live-offering
        // fallback. Best-effort inside, so a job list never fails over a missing
        // offering document.
        var jobs = await pendingJobEnricher.EnrichAsync(page.Jobs, cancellationToken);

        return new ChatConversationJobs(
            conversationId,
            detail.Conversation.ProviderId,
            detail.Conversation.PetParentId,
            jobs,
            page.TotalCount,
            normalizedSkip,
            normalizedTake);
    }

    /// <summary>
    /// Fills in the counterparty avatars SQL could not supply.
    ///
    /// A provider's image lives in their Cosmos offering document — the business
    /// image for a shop / hotel / clinic, the freelancer's own photo otherwise —
    /// because <c>Provider.Providers</c> has no photo column. That is the same
    /// split the booking detail's <c>providerPhotoUrl</c> and the pending-job
    /// card's <c>providerProfilePhotoUrl</c> live with, and the avatar here is
    /// resolved from the same source deliberately: the face beside a thread should
    /// be the face beside the booking it is about.
    ///
    /// ONE point read per DISTINCT provider on the page, issued together — a
    /// parent's inbox is very often several threads with the same few providers,
    /// and a page is capped at <see cref="ChatLimits.MaxConversationPageSize"/>.
    ///
    /// Best-effort, like every other enrichment of this shape: an inbox that
    /// renders with a missing avatar is a far better outcome than one that 500s
    /// because a Cosmos read failed, and a provider legitimately has no document
    /// until they save an offering.
    /// </summary>
    private async Task<IReadOnlyList<ChatConversationCard>> WithProviderPhotosAsync(
        IReadOnlyList<ChatConversationCard> cards,
        CancellationToken cancellationToken)
    {
        var pending = cards
            .Select(card => card.Counterparty)
            .Where(NeedsProviderPhoto)
            .Select(counterparty => (counterparty.ParticipantId, counterparty.ServiceCategory!))
            .Distinct()
            .ToList();

        if (pending.Count == 0)
        {
            return cards;
        }

        var photos = new Dictionary<Guid, string?>();

        var lookups = pending.Select(async entry =>
            (entry.ParticipantId, Photo: await ReadProviderPhotoAsync(
                entry.ParticipantId, entry.Item2, cancellationToken)));

        foreach (var (providerId, photo) in await Task.WhenAll(lookups))
        {
            photos[providerId] = photo;
        }

        return cards
            .Select(card =>
                photos.TryGetValue(card.Counterparty.ParticipantId, out var photo) && photo is not null
                    ? card with { Counterparty = card.Counterparty with { PhotoUrl = photo } }
                    : card)
            .ToList();
    }

    /// <summary>Single-thread twin of <see cref="WithProviderPhotosAsync"/>.</summary>
    private async Task<ChatCounterparty> WithProviderPhotoAsync(
        ChatCounterparty counterparty,
        CancellationToken cancellationToken)
    {
        if (!NeedsProviderPhoto(counterparty))
        {
            return counterparty;
        }

        var photo = await ReadProviderPhotoAsync(
            counterparty.ParticipantId, counterparty.ServiceCategory!, cancellationToken);

        return photo is null ? counterparty : counterparty with { PhotoUrl = photo };
    }

    /// <summary>
    /// A provider counterparty with no photo yet and a category to look one up in.
    /// A pet parent's photo already came from SQL, so they are never touched.
    /// </summary>
    private static bool NeedsProviderPhoto(ChatCounterparty counterparty) =>
        counterparty.ParticipantType == ChatParticipantType.Provider
        && string.IsNullOrWhiteSpace(counterparty.PhotoUrl)
        && !string.IsNullOrWhiteSpace(counterparty.ServiceCategory);

    private async Task<string?> ReadProviderPhotoAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await providerDiscovery.GetSummaryAsync(
                providerId, serviceCategory, cancellationToken);

            return summary?.ImageUrl;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not resolve the chat avatar for provider {ProviderId}; the thread is returned without one.",
                providerId);

            return null;
        }
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
        var detail = await conversationStore.GetForParticipantAsync(conversationId, participant, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        return await messageStore.ListAsync(
            conversationId,
            beforeSequence,
            // The caller's own "delete chat" watermark. It has to be pushed into
            // the query rather than filtered out of the result: the bodies are
            // shared with the counterparty, who still sees all of them, so this is
            // the only place the two views diverge. 0 for anyone who never cleared
            // the thread, which filters nothing.
            detail.Me.ClearedUpToSequence,
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
        var detail = await conversationStore.GetForParticipantAsync(conversationId, sender, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        // The legal hold. An open ticket on this thread freezes it for BOTH parties,
        // not just the reporter — the accused is the one with a motive to erase, so a
        // hold binding only the person who raised it would be decorative.
        //
        // Checked BEFORE the retraction, unlike the two best-effort legs below: this
        // one must be able to refuse, and a soft delete is not undoable once the
        // Cosmos document has been replaced. A failure to read the hold therefore
        // propagates rather than being swallowed — treating "I could not tell" as
        // "not held" would let the check be defeated by an outage.
        var hold = await legalHoldReader.GetConversationHoldAsync(conversationId, cancellationToken);
        if (hold is not null)
        {
            throw new Support.ConversationUnderLegalHoldException(conversationId, hold.TicketNumber);
        }

        // Scoped to the sender inside the store, so "not yours" and "no such
        // message" come back as the same null and cannot be told apart.
        var deleted = await messageStore.SoftDeleteAsync(conversationId, messageId, sender, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId);

        // The content is gone from Cosmos, but the inbox preview is a denormalised
        // copy in SQL and still holds it — so the retracted text would go on being
        // legible on the conversation card. The store's sequence guard decides
        // whether this message is still the one the card describes.
        //
        // Best-effort, and deliberately after the retraction rather than before:
        // the delete has already succeeded, so failing the request here would tell
        // the caller their message is still there when it is not. A stale preview
        // is the lesser fault, it is visible only until the next message, and the
        // alternative ordering is worse — a preview reading "deleted" over a
        // message that then failed to delete.
        try
        {
            await conversationStore.RefreshDeletedMessagePreviewAsync(
                conversationId, deleted.Sequence, ChatLimits.DeletedPreviewLabel, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Message {MessageId} was retracted but conversation {ConversationId}'s inbox preview " +
                "could not be updated; it will still show the deleted text until the next message.",
                messageId,
                conversationId);
        }

        // Tell the other side. Without this the retracted message sits on their
        // open screen until they happen to re-fetch — for a delete, the one
        // outcome nobody forgives. Best-effort and after the fact for the same
        // reason the send's fan-out is: the retraction is already durable, and a
        // socket failure must not report a delete that happened as failed. The
        // client reconciles on reconnect, where the message reads isDeleted.
        try
        {
            await realtimePublisher.PublishMessageDeletedAsync(
                deleted,
                new ChatParticipant(
                    detail.Counterparty.ParticipantType,
                    detail.Counterparty.ParticipantId),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Realtime fan-out failed for the retraction of message {MessageId}; it is stored and will be " +
                "picked up on reconnect.",
                messageId);
        }

        return deleted;
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

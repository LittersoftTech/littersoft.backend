using Pawfront.Application.Blocks;
using System.Collections.Concurrent;
using Pawfront.Application.Chat;

namespace Pawfront.Infrastructure.Sql.Chat;

/// <summary>
/// Dict-backed chat index for the in-memory development configuration.
///
/// FUNCTIONAL, not a zeros-reporting Null store like <c>NullProviderEarningsStore</c>
/// — chat is a write flow, so a store that accepted a message and never showed it
/// again would make the whole feature untestable without SQL. Same call the
/// review store made.
///
/// Two things it cannot do, both because it can only see itself:
///   * it never queues a notification (there is no outbox), so
///     <see cref="ChatAppendResult.NotificationId"/> is always null and a
///     developer on this configuration gets no pushes — the same posture the
///     booking sweeps take by having no in-memory equivalent at all;
///   * it cannot check that a provider or pet parent actually exists, so opening
///     a thread with a made-up id succeeds here and would 404 against SQL.
/// </summary>
internal sealed class InMemoryChatConversationStore(
    IChatPresenceStore presenceStore,
    // Blocks are no longer this store's to hold -- they moved to InMemoryBlockStore
    // with the rest of the feature. The dependency stays because a block still
    // closes a thread, and dropping the check here would quietly make in-memory
    // dev the one place a blocked pair can still message each other.
    IBlockStore blockStore)
    : IChatConversationStore
{
    private readonly ConcurrentDictionary<Guid, ConversationEntry> conversations = new();
    private readonly Lock gate = new();

    public async Task<ChatConversationDetail> GetOrCreateAsync(
        Guid providerId,
        Guid petParentId,
        ChatParticipant actor,
        CancellationToken cancellationToken)
    {
        if (await IsBlockedBetweenAsync(providerId, petParentId, cancellationToken))
        {
            throw new ChatBlockedException();
        }

        lock (gate)
        {
            var entry = conversations.Values.FirstOrDefault(
                c => c.ProviderId == providerId && c.PetParentId == petParentId);

            if (entry is null)
            {
                entry = new ConversationEntry(Guid.NewGuid(), providerId, petParentId, DateTimeOffset.UtcNow);
                conversations[entry.ConversationId] = entry;
            }
            else
            {
                // Re-opening a thread the caller had cleared puts it back on their
                // inbox, exactly as the procedure does. The watermark stays: they
                // deleted that history and walking back in is not a request for it.
                entry.StateFor(actor.Type).DeletedAtUtc = null;
            }

            return entry.ToDetail(actor);
        }
    }

    public async Task<ChatConversationDetail?> GetForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        var entry = Find(conversationId, participant);
        if (entry is null)
        {
            return null;
        }

        var detail = entry.ToDetail(participant);
        return detail with
        {
            Block = await ReadBlockStateAsync(
                participant, entry.CounterpartyIdFor(participant), cancellationToken)
        };
    }

    public async Task<IReadOnlyList<ChatConversationCard>> ListAsync(
        ChatParticipant participant,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // One read for the whole page, as the procedure does it in one query.
        var blockedIds = (await blockStore.ListMyBlockedCounterpartiesAsync(
                new BlockParty(
                    participant.Type == ChatParticipantType.Provider
                        ? BlockPartyType.Provider
                        : BlockPartyType.PetParent,
                    participant.Id),
                cancellationToken))
            .ToDictionary(b => b.CounterpartyId, b => b.BlockedByMe);

        IReadOnlyList<ChatConversationCard> cards = conversations.Values
            .Where(c => c.Involves(participant))
            // Mirrors the procedure's visibility rule: a thread this side has
            // cleared stays hidden until the counterparty writes again.
            .Where(c => !c.StateFor(participant.Type).IsCleared(c.LastSequence))
            // Only the preview is searchable here — this store cannot see the
            // profile tables, so it has no counterparty name to match on (the same
            // gap that leaves the card unnamed). Against SQL both are matched.
            .Where(c => string.IsNullOrWhiteSpace(search)
                        || (c.LastMessagePreview?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(c => c.LastMessageAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.ConversationId)
            .Skip(skip)
            .Take(take)
            .Select(c =>
            {
                var detail = c.ToDetail(participant);
                return new ChatConversationCard(
                    detail.Conversation,
                    detail.Me,
                    detail.Counterparty,
                    MyTicket: null,
                    Block: blockedIds.TryGetValue(c.CounterpartyIdFor(participant), out var mine)
                        ? new ChatBlockState(true, mine)
                        : new ChatBlockState(false, false));
            })
            .ToList();

        return cards;
    }

    /// <summary>
    /// The block state for one thread. Read from the block store rather than
    /// assumed absent, so a developer working without SQL sees the same flags the
    /// real thing returns -- which is the whole reason this store is functional
    /// rather than a no-op.
    /// </summary>
    private async Task<ChatBlockState> ReadBlockStateAsync(
        ChatParticipant participant, Guid counterpartyId, CancellationToken cancellationToken)
    {
        var blocked = await blockStore.ListMyBlockedCounterpartiesAsync(
            new BlockParty(
                participant.Type == ChatParticipantType.Provider
                    ? BlockPartyType.Provider
                    : BlockPartyType.PetParent,
                participant.Id),
            cancellationToken);

        var match = blocked.FirstOrDefault(b => b.CounterpartyId == counterpartyId);
        return match is null
            ? new ChatBlockState(false, false)
            : new ChatBlockState(true, match.BlockedByMe);
    }

    public Task<ChatParticipantState?> DeleteForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        var entry = Find(conversationId, participant);
        if (entry is null)
        {
            return Task.FromResult<ChatParticipantState?>(null);
        }

        lock (gate)
        {
            var state = entry.StateFor(participant.Type);

            state.ClearedUpToSequence = entry.LastSequence;
            state.DeletedAtUtc = DateTimeOffset.UtcNow;
            state.UnreadCount = 0;
            if (state.LastReadSequence < entry.LastSequence)
            {
                state.LastReadSequence = entry.LastSequence;
            }

            return Task.FromResult<ChatParticipantState?>(new ChatParticipantState(
                participant.Type,
                participant.Id,
                state.LastReadSequence,
                state.UnreadCount,
                state.IsMuted,
                state.ClearedUpToSequence,
                state.DeletedAtUtc));
        }
    }

    /// <summary>
    /// Phase one, mirroring <c>Chat.ReserveMessageSequence</c>: take the sequence
    /// and record the reservation, and touch nothing a caller could observe.
    /// </summary>
    public async Task<ChatMessageReservation> ReserveMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var entry = await RequireSendableAsync(conversationId, sender, cancellationToken);

        lock (gate)
        {
            // The dictionary key stands in for the SQL primary key on
            // (ConversationId, MessageId), so a duplicate id gets the original
            // sequence back here too.
            if (entry.Reservations.TryGetValue(messageId, out var existing))
            {
                return new ChatMessageReservation(
                    conversationId,
                    messageId,
                    existing.Sequence,
                    existing.ReservedAtUtc,
                    IsReplay: true,
                    IsCommitted: existing.IsCommitted);
            }

            var reservation = new MessageReservation(
                ++entry.LastSequence, sender.Type, DateTimeOffset.UtcNow);

            entry.Reservations[messageId] = reservation;

            return new ChatMessageReservation(
                conversationId,
                messageId,
                reservation.Sequence,
                reservation.ReservedAtUtc,
                IsReplay: false,
                IsCommitted: false);
        }
    }

    /// <summary>
    /// Phase two, mirroring <c>Chat.CommitMessageAppend</c>: everything a caller
    /// can observe, applied once and only once.
    /// </summary>
    public Task<ChatAppendResult> CommitMessageAsync(
        Guid conversationId,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken)
    {
        var entry = conversations.TryGetValue(conversationId, out var found)
            ? found
            : throw new ConversationNotFoundException(conversationId);

        long sequence;
        DateTimeOffset createdAtUtc;
        int recipientUnread;
        bool recipientIsMuted;
        ChatParticipantType senderType;

        lock (gate)
        {
            if (!entry.Reservations.TryGetValue(messageId, out var reservation))
            {
                throw new ConversationNotFoundException(conversationId);
            }

            sequence = reservation.Sequence;
            createdAtUtc = reservation.ReservedAtUtc;
            senderType = reservation.SenderType;

            if (!reservation.IsCommitted)
            {
                reservation.IsCommitted = true;

                // Only when nothing newer has committed, so an out-of-order commit
                // cannot pull the inbox card back to an older message.
                if (!entry.Reservations.Values.Any(r => r.IsCommitted && r.Sequence > sequence))
                {
                    entry.LastMessageAtUtc = createdAtUtc;
                    entry.LastMessagePreview = preview;
                    entry.LastMessageSenderType = senderType.ToSqlValue();
                }

                var senderState = entry.StateFor(senderType);
                if (sequence > senderState.LastReadSequence)
                {
                    senderState.LastReadSequence = sequence;
                }

                senderState.UnreadCount = 0;
                entry.StateFor(senderType.Counterparty()).UnreadCount++;
            }

            var recipientState = entry.StateFor(senderType.Counterparty());
            recipientUnread = recipientState.UnreadCount;
            recipientIsMuted = recipientState.IsMuted;
        }

        var recipientType = senderType.Counterparty();
        var recipientId = recipientType == ChatParticipantType.Provider
            ? entry.ProviderId
            : entry.PetParentId;

        var isViewing = presenceStore is InMemoryChatPresenceStore inMemoryPresence
                        && inMemoryPresence.IsViewing(recipientType, recipientId, conversationId);

        _ = cancellationToken;

        return Task.FromResult(new ChatAppendResult(
            conversationId,
            messageId,
            sequence,
            createdAtUtc,
            recipientType,
            recipientId,
            recipientUnread,
            isViewing,
            recipientIsMuted,
            // No outbox here — see the class remarks.
            NotificationId: null,
            RecipientTokens: [],
            NotificationDataJson: null));
    }

    public Task ReleaseMessageReservationAsync(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        if (conversations.TryGetValue(conversationId, out var entry))
        {
            lock (gate)
            {
                // A committed reservation describes a real message and is never
                // released — same rule as the procedure.
                if (entry.Reservations.TryGetValue(messageId, out var reservation)
                    && !reservation.IsCommitted)
                {
                    entry.Reservations.Remove(messageId);
                }
            }
        }

        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task RefreshDeletedMessagePreviewAsync(
        Guid conversationId,
        long sequence,
        string preview,
        CancellationToken cancellationToken)
    {
        if (conversations.TryGetValue(conversationId, out var entry))
        {
            lock (gate)
            {
                // Same guard as the procedure: only while this is still the last
                // message, so a send that raced the delete keeps its preview.
                if (entry.LastSequence == sequence)
                {
                    entry.LastMessagePreview = preview;
                }
            }
        }

        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private async Task<ConversationEntry> RequireSendableAsync(
        Guid conversationId, ChatParticipant sender, CancellationToken cancellationToken)
    {
        var entry = conversations.TryGetValue(conversationId, out var found)
            ? found
            : throw new ConversationNotFoundException(conversationId);

        if (!entry.Involves(sender))
        {
            throw new ChatForbiddenException(conversationId);
        }

        if (await IsBlockedBetweenAsync(entry.ProviderId, entry.PetParentId, cancellationToken))
        {
            throw new ChatBlockedException();
        }

        return entry;
    }

    public Task<ChatParticipantState?> MarkReadAsync(
        Guid conversationId,
        ChatParticipant participant,
        long upToSequence,
        CancellationToken cancellationToken)
    {
        var entry = Find(conversationId, participant);
        if (entry is null)
        {
            return Task.FromResult<ChatParticipantState?>(null);
        }

        lock (gate)
        {
            var state = entry.StateFor(participant.Type);
            var clamped = Math.Min(upToSequence, entry.LastSequence);

            if (clamped > state.LastReadSequence)
            {
                state.LastReadSequence = clamped;
            }

            if (clamped >= entry.LastSequence)
            {
                state.UnreadCount = 0;
            }

            return Task.FromResult<ChatParticipantState?>(
                new ChatParticipantState(
                    participant.Type, participant.Id, state.LastReadSequence, state.UnreadCount, state.IsMuted));
        }
    }

    public Task<ChatUnreadSummary> GetUnreadSummaryAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        var mine = conversations.Values
            .Where(c => c.Involves(participant))
            .Select(c => c.StateFor(participant.Type).UnreadCount)
            .ToList();

        return Task.FromResult(new ChatUnreadSummary(mine.Sum(), mine.Count(u => u > 0)));
    }

    private ConversationEntry? Find(Guid conversationId, ChatParticipant participant) =>
        conversations.TryGetValue(conversationId, out var entry) && entry.Involves(participant)
            ? entry
            : null;

    /// <summary>
    /// Either direction closes the thread. Reads the block store rather than a
    /// local copy, so the same block a user placed through /blocks is the one
    /// enforced here.
    /// </summary>
    private async Task<bool> IsBlockedBetweenAsync(
        Guid providerId, Guid petParentId, CancellationToken cancellationToken)
    {
        var blocked = await blockStore.ListMyBlockedCounterpartiesAsync(
            new BlockParty(BlockPartyType.Provider, providerId), cancellationToken);

        return blocked.Any(b => b.CounterpartyId == petParentId);
    }

    private sealed class ConversationEntry(
        Guid conversationId,
        Guid providerId,
        Guid petParentId,
        DateTimeOffset createdAtUtc)
    {
        public Guid ConversationId { get; } = conversationId;
        public Guid ProviderId { get; } = providerId;
        public Guid PetParentId { get; } = petParentId;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

        public long LastSequence;
        public DateTimeOffset? LastMessageAtUtc;
        public string? LastMessagePreview;
        public string? LastMessageSenderType;

        /// <summary>
        /// Stands in for <c>Chat.ConversationMessages</c>: the key is the client's
        /// message id, so it enforces the same one-message-per-id rule the
        /// primary key does against SQL. Guarded by the store's own lock.
        /// </summary>
        public readonly Dictionary<Guid, MessageReservation> Reservations = [];

        private readonly ParticipantState providerState = new();
        private readonly ParticipantState parentState = new();

        public ParticipantState StateFor(ChatParticipantType type) =>
            type == ChatParticipantType.Provider ? providerState : parentState;

        public bool Involves(ChatParticipant participant) =>
            participant.Type == ChatParticipantType.Provider
                ? participant.Id == ProviderId
                : participant.Id == PetParentId;

        /// <summary>The other party's id, from the caller's point of view.</summary>
        public Guid CounterpartyIdFor(ChatParticipant participant) =>
            participant.Type == ChatParticipantType.Provider ? PetParentId : ProviderId;

        public ChatConversationDetail ToDetail(ChatParticipant me)
        {
            var conversation = new ChatConversation(
                ConversationId, ProviderId, PetParentId, LastSequence,
                LastMessageAtUtc, LastMessagePreview, LastMessageSenderType,
                CreatedAtUtc, LastMessageAtUtc ?? CreatedAtUtc);

            var state = StateFor(me.Type);
            var counterpartyType = me.Type.Counterparty();

            return new ChatConversationDetail(
                conversation,
                new ChatParticipantState(
                    me.Type, me.Id, state.LastReadSequence, state.UnreadCount, state.IsMuted,
                    state.ClearedUpToSequence, state.DeletedAtUtc),
                // No name: this store cannot see the profile tables, so the
                // counterparty is identified but unnamed. Against SQL it is a live
                // join, which is what makes deleted accounts read "Deleted User".
                new ChatCounterparty(
                    counterpartyType,
                    counterpartyType == ChatParticipantType.Provider ? ProviderId : PetParentId,
                    Name: null,
                    PhotoUrl: null));
        }
    }

    /// <summary>
    /// One row of <c>Chat.ConversationMessages</c>: a sequence claimed by a
    /// message id, and whether its send ever finished.
    /// </summary>
    private sealed class MessageReservation(
        long sequence,
        ChatParticipantType senderType,
        DateTimeOffset reservedAtUtc)
    {
        public long Sequence { get; } = sequence;
        public ChatParticipantType SenderType { get; } = senderType;
        public DateTimeOffset ReservedAtUtc { get; } = reservedAtUtc;

        public bool IsCommitted;
    }

    private sealed class ParticipantState
    {
        public long LastReadSequence;
        public int UnreadCount;

        // "Delete chat", this side only. See the column comments on
        // [Chat].[ConversationParticipants] for why both are needed.
        public long ClearedUpToSequence;
        public DateTimeOffset? DeletedAtUtc;

        /// <summary>
        /// Hidden from this side's inbox: they cleared it, and nothing has been
        /// said since. The same two-part test the procedure makes — the watermark
        /// alone would hide a brand-new thread, where both are 0.
        /// </summary>
        public bool IsCleared(long lastSequence) =>
            DeletedAtUtc is not null && lastSequence <= ClearedUpToSequence;

        // Read by the append path (a muted thread queues no push) but never
        // written anywhere: muting is modelled end to end — column, procedures,
        // response field — and has NO endpoint to set it yet. Initialised
        // explicitly so that stays a deliberate gap rather than a compiler warning
        // somebody silences later.
        public bool IsMuted = false;
    }
}

/// <summary>
/// In-memory presence for the development configuration. Also readable by
/// <see cref="InMemoryChatConversationStore"/>, which needs the same
/// "is the recipient looking at this thread" answer that
/// <c>Chat.CommitMessageAppend</c> computes inside its transaction against SQL.
/// </summary>
internal sealed class InMemoryChatPresenceStore : IChatPresenceStore
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> connections = new();

    public Task SaveConnectionAsync(
        string connectionId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        connections.AddOrUpdate(
            connectionId,
            _ => new ConnectionEntry(participant, null, DateTimeOffset.UtcNow),
            (_, existing) => existing with
            {
                Participant = participant,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow
            });

        return Task.CompletedTask;
    }

    public Task DeleteConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        connections.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }

    public Task SetActiveConversationAsync(
        string connectionId,
        ChatParticipant participant,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (connections.TryGetValue(connectionId, out var existing)
            && existing.Participant.Type == participant.Type
            && existing.Participant.Id == participant.Id)
        {
            connections[connectionId] = existing with
            {
                ActiveConversationId = conversationId,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    public Task<int> PurgeStaleAsync(int staleMinutes, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, staleMinutes));
        var stale = connections
            .Where(pair => pair.Value.LastHeartbeatAtUtc < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var connectionId in stale)
        {
            connections.TryRemove(connectionId, out _);
        }

        return Task.FromResult(stale.Count);
    }

    public bool IsViewing(ChatParticipantType type, Guid participantId, Guid conversationId) =>
        connections.Values.Any(c =>
            c.Participant.Type == type
            && c.Participant.Id == participantId
            && c.ActiveConversationId == conversationId);

    private sealed record ConnectionEntry(
        ChatParticipant Participant,
        Guid? ActiveConversationId,
        DateTimeOffset LastHeartbeatAtUtc)
    {
        public ChatParticipant Participant { get; set; } = Participant;
    }
}

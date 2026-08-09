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
internal sealed class InMemoryChatConversationStore(IChatPresenceStore presenceStore)
    : IChatConversationStore
{
    private readonly ConcurrentDictionary<Guid, ConversationEntry> conversations = new();
    private readonly ConcurrentDictionary<Guid, ChatBlock> blocks = new();
    private readonly Lock gate = new();

    public Task<ChatConversationDetail> GetOrCreateAsync(
        Guid providerId,
        Guid petParentId,
        ChatParticipant actor,
        CancellationToken cancellationToken)
    {
        if (IsBlockedBetween(providerId, petParentId))
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

            return Task.FromResult(entry.ToDetail(actor));
        }
    }

    public Task<ChatConversationDetail?> GetForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        var entry = Find(conversationId, participant);
        return Task.FromResult(entry?.ToDetail(participant));
    }

    public Task<IReadOnlyList<ChatConversationCard>> ListAsync(
        ChatParticipant participant,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatConversationCard> cards = conversations.Values
            .Where(c => c.Involves(participant))
            .OrderByDescending(c => c.LastMessageAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.ConversationId)
            .Skip(skip)
            .Take(take)
            .Select(c =>
            {
                var detail = c.ToDetail(participant);
                return new ChatConversationCard(detail.Conversation, detail.Me, detail.Counterparty);
            })
            .ToList();

        return Task.FromResult(cards);
    }

    public async Task<ChatAppendResult> AppendMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken)
    {
        var entry = conversations.TryGetValue(conversationId, out var found)
            ? found
            : throw new ConversationNotFoundException(conversationId);

        if (!entry.Involves(sender))
        {
            throw new ChatForbiddenException(conversationId);
        }

        if (IsBlockedBetween(entry.ProviderId, entry.PetParentId))
        {
            throw new ChatBlockedException();
        }

        long sequence;
        DateTimeOffset now;
        int recipientUnread;
        bool recipientIsMuted;
        var recipientType = sender.Type.Counterparty();
        var recipientId = recipientType == ChatParticipantType.Provider
            ? entry.ProviderId
            : entry.PetParentId;

        lock (gate)
        {
            now = DateTimeOffset.UtcNow;
            sequence = ++entry.LastSequence;
            entry.LastMessageAtUtc = now;
            entry.LastMessagePreview = preview;
            entry.LastMessageSenderType = sender.Type.ToSqlValue();

            var senderState = entry.StateFor(sender.Type);
            senderState.LastReadSequence = sequence;
            senderState.UnreadCount = 0;

            var recipientState = entry.StateFor(recipientType);
            recipientState.UnreadCount++;
            recipientUnread = recipientState.UnreadCount;
            recipientIsMuted = recipientState.IsMuted;
        }

        var isViewing = presenceStore is InMemoryChatPresenceStore inMemoryPresence
                        && inMemoryPresence.IsViewing(recipientType, recipientId, conversationId);

        _ = cancellationToken;
        await Task.CompletedTask;

        return new ChatAppendResult(
            conversationId,
            messageId,
            sequence,
            now,
            recipientType,
            recipientId,
            recipientUnread,
            isViewing,
            recipientIsMuted,
            // No outbox here — see the class remarks.
            NotificationId: null,
            RecipientTokens: [],
            NotificationDataJson: null);
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

    public Task<ChatBlock> BlockAsync(
        ChatParticipant blocker,
        ChatParticipantType blockedType,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (blocker.Type == blockedType)
        {
            throw new ChatInvalidBlockException();
        }

        var existing = blocks.Values.FirstOrDefault(
            b => b.BlockerType == blocker.Type && b.BlockerId == blocker.Id
                 && b.BlockedType == blockedType && b.BlockedId == blockedId);

        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        var block = new ChatBlock(
            Guid.NewGuid(), blocker.Type, blocker.Id, blockedType, blockedId,
            reason, null, null, DateTimeOffset.UtcNow);

        blocks[block.ChatBlockId] = block;
        return Task.FromResult(block);
    }

    public Task<ChatBlock?> UnblockAsync(
        Guid chatBlockId,
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        if (blocks.TryGetValue(chatBlockId, out var block)
            && block.BlockerType == blocker.Type
            && block.BlockerId == blocker.Id
            && blocks.TryRemove(chatBlockId, out _))
        {
            return Task.FromResult<ChatBlock?>(block);
        }

        return Task.FromResult<ChatBlock?>(null);
    }

    public Task<IReadOnlyList<ChatBlock>> ListBlocksAsync(
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatBlock> mine = blocks.Values
            .Where(b => b.BlockerType == blocker.Type && b.BlockerId == blocker.Id)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToList();

        return Task.FromResult(mine);
    }

    private ConversationEntry? Find(Guid conversationId, ChatParticipant participant) =>
        conversations.TryGetValue(conversationId, out var entry) && entry.Involves(participant)
            ? entry
            : null;

    private bool IsBlockedBetween(Guid providerId, Guid petParentId) =>
        blocks.Values.Any(b =>
            (b.BlockerType == ChatParticipantType.Provider && b.BlockerId == providerId && b.BlockedId == petParentId)
            || (b.BlockerType == ChatParticipantType.PetParent && b.BlockerId == petParentId && b.BlockedId == providerId));

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

        private readonly ParticipantState providerState = new();
        private readonly ParticipantState parentState = new();

        public ParticipantState StateFor(ChatParticipantType type) =>
            type == ChatParticipantType.Provider ? providerState : parentState;

        public bool Involves(ChatParticipant participant) =>
            participant.Type == ChatParticipantType.Provider
                ? participant.Id == ProviderId
                : participant.Id == PetParentId;

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
                new ChatParticipantState(me.Type, me.Id, state.LastReadSequence, state.UnreadCount, state.IsMuted),
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

    private sealed class ParticipantState
    {
        public long LastReadSequence;
        public int UnreadCount;

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
/// <c>Chat.AppendMessage</c> computes inside its transaction against SQL.
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

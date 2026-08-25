using System.Collections.Concurrent;
using Pawfront.Application.Blocks;

namespace Pawfront.Infrastructure.Sql.Blocks;

/// <summary>
/// The in-memory development fallback for blocking.
/// </summary>
/// <remarks>
/// <para>
/// Functional rather than a no-op, for the same reason reviews and chat are: this
/// is a write flow, and a store that accepted a block and then never reported it
/// would make the whole feature untestable without SQL — including the flags that
/// hang off it, which are the part most likely to be got wrong.
/// </para>
/// <para>
/// <b>One thing it cannot do:</b> return the pair's unfinished jobs, because it
/// cannot see the booking tables. So a block placed here severs the pair but
/// cancels nothing, and <see cref="BlockService"/> reports no cancelled jobs. That
/// is the same limitation every in-memory store here carries for anything needing
/// a second table, and it is called out because the auto-cancel is the one part of
/// this feature a developer might otherwise assume they had exercised.
/// </para>
/// </remarks>
internal sealed class InMemoryBlockStore : IBlockStore, IMyBlockLookup
{
    private readonly ConcurrentDictionary<BlockKey, ParticipantBlock> blocks = new();

    private readonly record struct BlockKey(
        BlockPartyType BlockerType, Guid BlockerId, BlockPartyType BlockedType, Guid BlockedId);

    public Task<(ParticipantBlock Block, bool WasAlreadyBlocked, IReadOnlyList<BlockCancellableJob> Jobs)>
        BlockAsync(
            BlockParty blocker,
            BlockPartyType blockedType,
            Guid blockedId,
            string? reason,
            CancellationToken cancellationToken)
    {
        if (blocker.Type == blockedType)
        {
            throw new InvalidBlockPairException("A block must run between a provider and a pet parent.");
        }

        var key = new BlockKey(blocker.Type, blocker.Id, blockedType, blockedId);
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        var wasAlreadyBlocked = true;
        var block = blocks.GetOrAdd(key, _ =>
        {
            wasAlreadyBlocked = false;
            return new ParticipantBlock(
                BlockId: Guid.NewGuid(),
                BlockerType: blocker.Type,
                BlockerId: blocker.Id,
                BlockedType: blockedType,
                BlockedId: blockedId,
                Reason: trimmed,
                BlockedName: null,
                BlockedBusinessName: null,
                BlockedPhotoUrl: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        });

        // Always empty — see the class remarks.
        return Task.FromResult<(ParticipantBlock, bool, IReadOnlyList<BlockCancellableJob>)>(
            (block, wasAlreadyBlocked, Array.Empty<BlockCancellableJob>()));
    }

    public Task<ParticipantBlock?> UnblockAsync(
        Guid blockId,
        BlockParty blocker,
        CancellationToken cancellationToken)
    {
        // Scoped to the caller as blocker, exactly as the procedure is: an
        // unknown id and somebody else's block are one case.
        var match = blocks
            .Where(pair => pair.Value.BlockId == blockId
                           && pair.Value.BlockerType == blocker.Type
                           && pair.Value.BlockerId == blocker.Id)
            .Select(pair => (KeyValuePair<BlockKey, ParticipantBlock>?)pair)
            .FirstOrDefault();

        if (match is null || !blocks.TryRemove(match.Value.Key, out var removed))
        {
            return Task.FromResult<ParticipantBlock?>(null);
        }

        return Task.FromResult<ParticipantBlock?>(removed);
    }

    public Task<(IReadOnlyList<ParticipantBlockRow> Rows, int TotalCount)> ListAsync(
        BlockParty blocker,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var mine = blocks.Values
            .Where(b => b.BlockerType == blocker.Type && b.BlockerId == blocker.Id)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.BlockId)
            .ToList();

        var rows = mine
            .Skip(skip)
            .Take(take)
            // No profile tables here, so no names and no service category — which
            // also means the Cosmos business-name lookup is never attempted.
            .Select(b => new ParticipantBlockRow(b, BlockedServiceCategory: null))
            .ToList();

        return Task.FromResult<(IReadOnlyList<ParticipantBlockRow>, int)>((rows, mine.Count));
    }

    public Task<IReadOnlyList<BlockedCounterparty>> ListMyBlockedCounterpartiesAsync(
        BlockParty participant,
        CancellationToken cancellationToken)
    {
        var placed = blocks.Values
            .Where(b => b.BlockerType == participant.Type && b.BlockerId == participant.Id)
            .Select(b => new BlockedCounterparty(b.BlockedType, b.BlockedId, true, b.BlockId, b.CreatedAtUtc));

        var received = blocks.Values
            .Where(b => b.BlockedType == participant.Type && b.BlockedId == participant.Id)
            // The id is deliberately not carried through: the caller cannot lift
            // somebody else's block, and handing it over would confirm they acted.
            .Select(b => new BlockedCounterparty(b.BlockerType, b.BlockerId, false, null, b.CreatedAtUtc));

        // Both directions collapse to one row per counterparty, mine winning, so
        // a mutual block still offers the Unblock button for the caller's own.
        var collapsed = placed.Concat(received)
            .GroupBy(c => c.CounterpartyId)
            .Select(g => g.OrderByDescending(c => c.BlockedByMe).First())
            .ToList();

        return Task.FromResult<IReadOnlyList<BlockedCounterparty>>(collapsed);
    }

    async Task<MyBlockedCounterparties> IMyBlockLookup.GetAsync(
        BlockParty participant,
        CancellationToken cancellationToken)
    {
        var counterparties = await ListMyBlockedCounterpartiesAsync(participant, cancellationToken);
        return counterparties.Count == 0
            ? MyBlockedCounterparties.Empty
            : new MyBlockedCounterparties(counterparties);
    }
}

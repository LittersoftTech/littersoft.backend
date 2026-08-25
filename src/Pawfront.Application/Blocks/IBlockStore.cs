namespace Pawfront.Application.Blocks;

/// <summary>
/// The SQL side of blocking. Deliberately narrow: it writes and reads
/// <c>Block.BlockedParticipants</c> and nothing else. Cancelling the pair's
/// unfinished jobs, and resolving a blocked provider's business name from Cosmos,
/// both belong to <see cref="IBlockService"/> -- neither is something SQL can do.
/// </summary>
public interface IBlockStore
{
    /// <summary>
    /// Places the block and returns it together with the pair's unfinished jobs,
    /// captured inside the same transaction.
    /// </summary>
    /// <remarks>
    /// Idempotent -- blocking somebody already blocked returns the existing row
    /// with <c>WasAlreadyBlocked</c> true and an EMPTY job list, because the first
    /// block already cancelled them.
    /// </remarks>
    /// <exception cref="InvalidBlockPairException">
    /// Both parties are on the same side.
    /// </exception>
    Task<(ParticipantBlock Block, bool WasAlreadyBlocked, IReadOnlyList<BlockCancellableJob> Jobs)>
        BlockAsync(
            BlockParty blocker,
            BlockPartyType blockedType,
            Guid blockedId,
            string? reason,
            CancellationToken cancellationToken);

    /// <summary>
    /// Lifts a block the caller placed. Null when the id is unknown OR belongs to
    /// somebody else's block -- deliberately the same answer, so a block id cannot
    /// be probed for existence.
    /// </summary>
    Task<ParticipantBlock?> UnblockAsync(
        Guid blockId,
        BlockParty blocker,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the blocks the caller PLACED, newest first. Blocks placed
    /// against them are never returned.
    /// </summary>
    Task<(IReadOnlyList<ParticipantBlockRow> Rows, int TotalCount)> ListAsync(
        BlockParty blocker,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every counterparty the caller is blocked from, in either direction.
    /// </summary>
    Task<IReadOnlyList<BlockedCounterparty>> ListMyBlockedCounterpartiesAsync(
        BlockParty participant,
        CancellationToken cancellationToken);
}

/// <summary>
/// The per-request "who am I blocked from" read, behind its own interface so the
/// booking, event and discovery paths can depend on the lookup without taking the
/// whole write-side of blocking with it.
/// </summary>
/// <remarks>
/// <b>Best-effort by contract.</b> Every implementation returns
/// <see cref="MyBlockedCounterparties.Empty"/> rather than throwing, because every
/// caller is decorating something that has already succeeded: a booking list, an
/// event page, a conversation. A blocked-table hiccup should cost a flag, never
/// the page.
/// </remarks>
public interface IMyBlockLookup
{
    Task<MyBlockedCounterparties> GetAsync(
        BlockParty participant,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Blocks;

/// <summary>
/// Blocking, product-wide.
/// </summary>
/// <remarks>
/// <para>
/// A block began as a chat remedy and is not one any more. From a single row it
/// stops messages, refuses new bookings in both directions, hides each party's
/// events from the other, and takes the provider out of browse and all five
/// searches. It is placed by a user, and it is theirs alone to lift.
/// </para>
/// <para>
/// <b>Placing one cancels the pair's unfinished jobs.</b> That is the part worth
/// knowing before calling this: blocking is not a passive setting, it ends work
/// the counterparty had agreed to do. The exception is a job already underway --
/// the pet is in someone's care, and the status engine refuses that cancel
/// (THROW 51149) rather than being bypassed. Such a job is reported back in
/// <see cref="BlockResult.Failed"/> so the caller can say so instead of leaving
/// the user to discover it.
/// </para>
/// <para>
/// <b>What it does NOT do:</b> delete chat history (it is both parties' record,
/// and the evidence a blocked user might need in order to report), hide a
/// provider behind a booking the parent already has, or take away an event ticket
/// already paid for.
/// </para>
/// </remarks>
public interface IBlockService
{
    /// <summary>
    /// Blocks the counterparty and cancels the pair's unfinished jobs.
    /// </summary>
    /// <param name="blockedId">
    /// The counterparty's id. Their SIDE is derived from the caller's, never
    /// passed in -- a block only ever runs provider &lt;-&gt; parent, so accepting
    /// it would only create a way to get it wrong.
    /// </param>
    /// <remarks>
    /// Idempotent. Blocking somebody already blocked returns the existing block
    /// with <see cref="BlockResult.WasAlreadyBlocked"/> true and no jobs, because
    /// the first block already dealt with them.
    /// </remarks>
    Task<BlockResult> BlockAsync(
        BlockParty blocker,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lifts a block the caller placed. Null when the id is unknown or is not
    /// theirs -- the same answer either way, so an id cannot be probed.
    /// </summary>
    /// <remarks>
    /// Restores contact only. The bookings the block cancelled stay cancelled:
    /// they went through the ordinary transition, with an audit row and their
    /// capacity released back to the provider's calendar, which may since have
    /// been sold to somebody else. Re-booking is a new booking.
    /// </remarks>
    Task<ParticipantBlock?> UnblockAsync(
        Guid blockId,
        BlockParty blocker,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the people the caller has blocked, newest first, with a
    /// blocked provider's business name resolved alongside their own.
    /// </summary>
    /// <remarks>
    /// Only blocks the caller PLACED. One placed against them is never listed:
    /// telling somebody they have been blocked confirms the other party acted.
    /// </remarks>
    Task<ParticipantBlockPage> ListAsync(
        BlockParty blocker,
        int skip,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>The bounds the blocked list is built to.</summary>
public static class BlockListLimits
{
    /// <summary>
    /// Matches the cap every other paged list here uses (reviews, earnings,
    /// spend, support tickets, the chat inbox).
    /// </summary>
    public const int MaxTake = 20;

    public const int DefaultTake = 20;

    public static int NormalizeTake(int? take) =>
        take is null or < 1 ? DefaultTake : Math.Min(take.Value, MaxTake);

    public static int NormalizeSkip(int? skip) => skip is null or < 0 ? 0 : skip.Value;
}

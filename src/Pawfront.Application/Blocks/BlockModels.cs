namespace Pawfront.Application.Blocks;

/// <summary>
/// Which side of a blockable relationship somebody is. A block always runs
/// provider &lt;-&gt; parent, so a pair has exactly one of each.
/// </summary>
/// <remarks>
/// This deliberately mirrors <see cref="Chat.ChatParticipantType"/> without being
/// it. The two agree today and probably always will, but they answer different
/// questions: that one says "whose row is this in <c>Chat.ConversationParticipants</c>",
/// this one says "which column of <c>Block.BlockedParticipants</c> is this id in".
/// Blocking stopped being a chat feature when it started refusing bookings and
/// hiding events, and a block placed from the bookings screen should not have to
/// name itself in the chat module's vocabulary to be stored. The chat host
/// converts at its one boundary; nothing else needs to.
/// </remarks>
public enum BlockPartyType
{
    /// <summary>A service provider, identified by their <c>ProviderId</c>.</summary>
    Provider,

    /// <summary>A pet parent, identified by their <c>PetParentId</c>.</summary>
    PetParent
}

/// <summary>One end of a block: which side, and which id on that side.</summary>
public readonly record struct BlockParty(BlockPartyType Type, Guid Id);

public static class BlockPartyTypes
{
    /// <summary>
    /// The literal stored in <c>Block.BlockedParticipants.BlockerType</c> /
    /// <c>BlockedType</c> and checked by their CHECK constraints. Explicit rather
    /// than <see cref="Enum.ToString()"/> so renaming a member can never silently
    /// change what is written to the database.
    /// </summary>
    public static string ToSqlValue(this BlockPartyType type) => type switch
    {
        BlockPartyType.Provider => "Provider",
        BlockPartyType.PetParent => "PetParent",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown block party type.")
    };

    public static BlockPartyType FromSqlValue(string value) => value switch
    {
        "Provider" => BlockPartyType.Provider,
        "PetParent" => BlockPartyType.PetParent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown block party type.")
    };

    /// <summary>The other side. A block never runs within one side.</summary>
    public static BlockPartyType Counterparty(this BlockPartyType type) => type switch
    {
        BlockPartyType.Provider => BlockPartyType.PetParent,
        BlockPartyType.PetParent => BlockPartyType.Provider,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown block party type.")
    };
}

/// <summary>
/// A block the caller placed, as shown on their blocked list.
/// </summary>
/// <param name="BlockedName">
/// The blocked party's personal name, joined LIVE rather than denormalised onto
/// the block — so somebody who has since deleted their account reads "Deleted
/// Provider" / "Deleted User" instead of leaving their real name frozen here.
/// </param>
/// <param name="BlockedBusinessName">
/// The blocked PROVIDER's business name ("Happy Paws Hotel"), resolved from their
/// Cosmos offering document. Null for a blocked pet parent, who has no business,
/// and for a freelance provider, who trades under their own name -- and null too
/// when the offering cannot be read, which is best-effort by design: a blocked
/// list that fails because one provider is mid-onboarding would be worse than one
/// missing a business name.
/// </param>
/// <param name="BlockedPhotoUrl">
/// A parent's photo comes off their SQL row; a provider's comes from the same
/// Cosmos offering document as the business name.
/// </param>
public sealed record ParticipantBlock(
    Guid BlockId,
    BlockPartyType BlockerType,
    Guid BlockerId,
    BlockPartyType BlockedType,
    Guid BlockedId,
    string? Reason,
    string? BlockedName,
    string? BlockedBusinessName,
    string? BlockedPhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>One page of the caller's blocked list.</summary>
public sealed record ParticipantBlockPage(
    IReadOnlyList<ParticipantBlock> Blocks,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasMore => Skip + Blocks.Count < TotalCount;
}

/// <summary>
/// A row of <c>Block.BlockedParticipants</c> as SQL hands it back, before the
/// business name and provider image are resolved from Cosmos.
/// </summary>
/// <param name="BlockedServiceCategory">
/// The blocked provider's Cosmos partition key, needed to point-read their
/// offering document. Null for a blocked parent, and for a provider who has
/// registered no service yet -- which is legitimate, since a provider can be
/// blocked while still onboarding.
/// </param>
public sealed record ParticipantBlockRow(
    ParticipantBlock Block,
    string? BlockedServiceCategory);

/// <summary>
/// An unfinished job between the pair, as captured by the same transaction that
/// wrote the block -- the input to the cancellation loop that follows.
/// </summary>
public sealed record BlockCancellableJob(
    Guid BookingId,
    string BookingType,
    int JobNumber,
    string Status,
    DateOnly ServiceDate);

/// <summary>
/// What one of the pair's unfinished jobs ended up doing when the block cancelled
/// it. <see cref="Cancelled"/> false carries <see cref="ErrorCode"/> instead --
/// most often <c>BookingInProgress</c>, the job that was underway and was
/// deliberately left to run.
/// </summary>
public sealed record BlockCancelledJob(
    Guid BookingId,
    string BookingType,
    string JobId,
    bool Cancelled,
    string? Status,
    string? ErrorCode,
    string? Message);

/// <summary>
/// The outcome of placing a block: the block itself, plus what happened to the
/// pair's unfinished work.
/// </summary>
/// <param name="WasAlreadyBlocked">
/// True when the block was already in place, in which case nothing was cancelled
/// -- the first block did that.
/// </param>
/// <param name="Jobs">
/// Every unfinished job the block touched, cancelled or not. A caller rendering a
/// confirmation reads <see cref="CancelledCount"/> for "3 bookings cancelled" and
/// the failures for anything that has to be explained -- a job in progress runs to
/// completion and the user should be told so rather than left to discover it.
/// </param>
public sealed record BlockResult(
    ParticipantBlock Block,
    bool WasAlreadyBlocked,
    IReadOnlyList<BlockCancelledJob> Jobs)
{
    public int CancelledCount => Jobs.Count(job => job.Cancelled);

    public IReadOnlyList<BlockCancelledJob> Failed =>
        Jobs.Where(job => !job.Cancelled).ToList();
}

/// <summary>
/// Every counterparty the caller is blocked from, resolved once per request.
/// </summary>
/// <remarks>
/// Scoped to the ACTOR rather than taking a list of ids, so a page of twenty
/// bookings costs one round trip instead of twenty -- the same shape
/// <c>MySupportTicketSubjects</c> and the parent's booking-review lookup take.
/// </remarks>
public sealed class MyBlockedCounterparties
{
    private readonly IReadOnlyDictionary<Guid, BlockedCounterparty> byId;

    public static MyBlockedCounterparties Empty { get; } =
        new(Array.Empty<BlockedCounterparty>());

    public MyBlockedCounterparties(IReadOnlyList<BlockedCounterparty> counterparties)
    {
        ArgumentNullException.ThrowIfNull(counterparties);

        // Ids are GUIDs and globally unique across both tables, so one dictionary
        // serves providers and parents alike without keying on the type too.
        byId = counterparties
            .GroupBy(c => c.CounterpartyId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public bool IsEmpty => byId.Count == 0;

    /// <summary>True when a block exists in EITHER direction.</summary>
    public bool IsBlocked(Guid counterpartyId) => byId.ContainsKey(counterpartyId);

    /// <summary>
    /// The block, when there is one. <see cref="BlockedCounterparty.BlockedByMe"/>
    /// is what decides whether the caller is offered an Unblock button or a
    /// neutral "not available".
    /// </summary>
    public BlockedCounterparty? Find(Guid counterpartyId) =>
        byId.TryGetValue(counterpartyId, out var found) ? found : null;

    public IReadOnlyCollection<Guid> BlockedIds => byId.Keys.ToList();
}

/// <summary>
/// One severed relationship, collapsed from however many directions it runs in.
/// </summary>
/// <param name="BlockedByMe">
/// True when the CALLER placed it. Only their own block offers an Unblock button,
/// which is why <see cref="BlockId"/> is populated only in that case: handing back
/// the id of a block placed against them would be useless -- they cannot lift it
/// -- and would confirm the other party acted, the one thing a block must not do.
/// </param>
public sealed record BlockedCounterparty(
    BlockPartyType CounterpartyType,
    Guid CounterpartyId,
    bool BlockedByMe,
    Guid? BlockId,
    DateTimeOffset BlockedAtUtc);

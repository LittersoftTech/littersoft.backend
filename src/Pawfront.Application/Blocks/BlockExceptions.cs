namespace Pawfront.Application.Blocks;

/// <summary>
/// Both parties are on the same side, so the block could never be consulted --
/// every rule that reads the table asks about a provider and a parent. Maps to
/// SQL THROW 51327 and, at the API, to 400 InvalidRequest. Defensive: the hosts
/// derive the blocked side from the caller's own, so a client cannot reach it.
/// </summary>
public sealed class InvalidBlockPairException(string message) : Exception(message);

/// <summary>
/// A booking was refused because the two parties are blocked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BlockedBy"/> is the whole point of this type. The refusal has to be
/// worded differently depending on who is asking, and only the API layer knows
/// that: if the CALLER placed the block it is named ("you blocked this provider
/// -- unblock them to book"), and if it was placed against them it must read as a
/// neutral "not available". Saying otherwise would confirm the other party acted,
/// which is exactly what a block is meant to end.
/// </para>
/// <para>
/// SQL therefore reports which side acted (THROW 51370 / 51371) and leaves the
/// wording to C#, because the create procedures are called by both hosts and have
/// no actor of their own to reason about.
/// </para>
/// </remarks>
public sealed class BookingBlockedException(BlockPartyType blockedBy, string message)
    : Exception(message)
{
    /// <summary>Which side placed the block.</summary>
    public BlockPartyType BlockedBy { get; } = blockedBy;

    /// <summary>
    /// True when <paramref name="caller"/> is the party that placed it, and may
    /// therefore be told about it and offered a way to lift it.
    /// </summary>
    public bool WasPlacedBy(BlockPartyType caller) => BlockedBy == caller;
}

/// <summary>
/// A ticket purchase was refused because the buyer and the event's organiser are
/// blocked. Separate from <see cref="BookingBlockedException"/> because it is a
/// different flow with a different remedy -- there is no service booking to
/// unblock and retry, just an event the buyer should not be seeing.
/// </summary>
/// <remarks>
/// In practice this is close to unreachable through the apps: the same block
/// hides the event from every list and 404s its detail, so a buyer would have to
/// hold an id from before the block. It exists so that holding the id is not
/// enough, which is the difference between hiding something and refusing it.
/// </remarks>
public sealed class EventBookingBlockedException(Guid eventId)
    : Exception("This event is not available.")
{
    public Guid EventId { get; } = eventId;
}

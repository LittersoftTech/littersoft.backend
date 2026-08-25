using Pawfront.Application.Blocks;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Resolves who the caller is blocked from, for the read paths that surface
/// <c>isBlocked</c> on a booking.
/// </summary>
/// <remarks>
/// <para>
/// One read serves a whole page, so a booking list pays for it once rather than
/// once per card -- the same shape <see cref="MySupportTickets"/> takes, and for
/// the same reason.
/// </para>
/// <para>
/// A booking outlives the block that severed its parties: the job happened, and
/// both sides keep it. The flag is what lets the app label such a booking instead
/// of leaving the parent to wonder why they can no longer message the provider
/// on it.
/// </para>
/// <para>
/// Best-effort by contract -- <see cref="IMyBlockLookup"/> returns
/// <see cref="MyBlockedCounterparties.Empty"/> rather than throwing, so a hiccup
/// costs a flag and never the bookings list.
/// </para>
/// </remarks>
internal static class MyBlocks
{
    public static Task<MyBlockedCounterparties> ForPetParentAsync(
        Guid petParentId,
        IMyBlockLookup lookup,
        CancellationToken cancellationToken)
        => petParentId == Guid.Empty
            ? Task.FromResult(MyBlockedCounterparties.Empty)
            : lookup.GetAsync(new BlockParty(BlockPartyType.PetParent, petParentId), cancellationToken);

    /// <summary>
    /// The block state for one booking, given the counterparty on it. A booking
    /// with no counterparty -- a Custom walk-in, which has no PetParentId -- can
    /// never be blocked, so it reads false/false.
    /// </summary>
    public static (bool IsBlocked, bool BlockedByMe) ForCounterparty(
        this MyBlockedCounterparties blocked, Guid? counterpartyId)
    {
        if (counterpartyId is not { } id)
        {
            return (false, false);
        }

        var match = blocked.Find(id);
        return match is null ? (false, false) : (true, match.BlockedByMe);
    }
}

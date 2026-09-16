using Pawfront.Application.Blocks;

namespace Pawfront.PetParentApi.Auth;

/// <summary>
/// The caller as the block module sees them, resolved from the JWT through the
/// same cached lookup every ownership-filtered route uses.
/// </summary>
/// <remarks>
/// This is what lets provider discovery and the five booking searches hide a
/// provider the caller is blocked from: the decorator needs to know who is asking,
/// and the discovery filter carries no caller of its own.
///
/// A parent who has not completed their profile resolves to null, which filters
/// nothing -- correct, since they can have placed no blocks and had none placed on
/// them. Registered BEFORE <c>AddPawfrontApplication()</c> so it wins the TryAdd
/// against the no-op default.
/// </remarks>
internal sealed class CurrentBlockParty(ICurrentPetParentContext currentPetParent) : ICurrentBlockParty
{
    public async Task<BlockParty?> GetAsync(CancellationToken cancellationToken)
    {
        var petParentId = await currentPetParent.GetPetParentIdAsync(cancellationToken);
        return petParentId is null
            ? null
            : new BlockParty(BlockPartyType.PetParent, petParentId.Value);
    }
}

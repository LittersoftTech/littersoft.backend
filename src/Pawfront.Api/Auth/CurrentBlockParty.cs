using Pawfront.Application.Blocks;

namespace Pawfront.Api.Auth;

internal sealed class CurrentBlockParty(ICurrentProviderContext currentProvider) : ICurrentBlockParty
{
    public async Task<BlockParty?> GetAsync(CancellationToken cancellationToken)
    {
        var id = await currentProvider.GetProviderIdAsync(cancellationToken);
        return id is null ? null : new BlockParty(BlockPartyType.Provider, id.Value);
    }
}

using Pawfront.Contracts.Reliability;

namespace Pawfront.Application.Reliability;

public interface IReliabilityReader
{
    Task<ReliabilityResponse> GetParentAsync(Guid petParentId, CancellationToken cancellationToken);
    Task<ReliabilityResponse> GetProviderAsync(Guid providerId, CancellationToken cancellationToken);
}

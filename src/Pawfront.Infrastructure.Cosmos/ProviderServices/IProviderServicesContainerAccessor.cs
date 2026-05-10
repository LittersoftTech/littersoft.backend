using Microsoft.Azure.Cosmos;

namespace Pawfront.Infrastructure.Cosmos.ProviderServices;

public interface IProviderServicesContainerAccessor
{
    Task<Container> GetContainerAsync(CancellationToken cancellationToken);
}

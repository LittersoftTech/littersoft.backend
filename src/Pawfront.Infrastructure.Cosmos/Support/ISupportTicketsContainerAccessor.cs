using Microsoft.Azure.Cosmos;

namespace Pawfront.Infrastructure.Cosmos.Support;

public interface ISupportTicketsContainerAccessor
{
    Task<Container> GetContainerAsync(CancellationToken cancellationToken);
}

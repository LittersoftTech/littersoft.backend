using Microsoft.Azure.Cosmos;

namespace Pawfront.Infrastructure.Cosmos.Events;

public interface IEventsContainerAccessor
{
    Task<Container> GetContainerAsync(CancellationToken cancellationToken);
}

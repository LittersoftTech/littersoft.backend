using Microsoft.Azure.Cosmos;

namespace Pawfront.Infrastructure.Cosmos.Chat;

public interface IChatMessagesContainerAccessor
{
    Task<Container> GetContainerAsync(CancellationToken cancellationToken);
}

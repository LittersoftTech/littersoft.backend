using Pawfront.Contracts.Services;

namespace Pawfront.Application.Services;

public interface IPetServiceCatalog
{
    Task<ServiceResponse> CreateAsync(Guid providerId, CreateServiceRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ServiceResponse>> ListByProviderAsync(Guid providerId, CancellationToken cancellationToken);
}

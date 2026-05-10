using Pawfront.Contracts.Providers;

namespace Pawfront.Application.Providers;

public interface IProviderService
{
    Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProviderResponse>> ListAsync(CancellationToken cancellationToken);
}

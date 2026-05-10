using System.Collections.Concurrent;
using Pawfront.Application.Providers;
using Pawfront.Contracts.Providers;
using Pawfront.Domain.Providers;

namespace Pawfront.Infrastructure.Sql.Providers;

internal sealed class InMemoryProviderService : IProviderService
{
    private readonly ConcurrentDictionary<Guid, ServiceProvider> providers = new();

    public Task<ProviderResponse> CreateAsync(CreateProviderRequest request, CancellationToken cancellationToken)
    {
        var provider = new ServiceProvider
        {
            BusinessName = request.BusinessName,
            OwnerName = request.OwnerName,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Status = ProviderStatus.Active
        };

        providers[provider.Id] = provider;

        return Task.FromResult(ToResponse(provider));
    }

    public Task<IReadOnlyCollection<ProviderResponse>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ProviderResponse> result = providers.Values
            .OrderBy(provider => provider.BusinessName)
            .Select(ToResponse)
            .ToArray();

        return Task.FromResult(result);
    }

    private static ProviderResponse ToResponse(ServiceProvider provider)
    {
        return new ProviderResponse(
            provider.Id,
            provider.BusinessName,
            provider.OwnerName,
            provider.Email,
            provider.PhoneNumber,
            provider.Status.ToString(),
            provider.CreatedAt);
    }
}

using System.Collections.Concurrent;
using Pawfront.Application.Services;
using Pawfront.Contracts.Services;
using Pawfront.Domain.Services;

namespace Pawfront.Infrastructure.Sql.Services;

internal sealed class InMemoryPetServiceCatalog : IPetServiceCatalog
{
    private readonly ConcurrentDictionary<Guid, PetService> services = new();

    public Task<ServiceResponse> CreateAsync(Guid providerId, CreateServiceRequest request, CancellationToken cancellationToken)
    {
        var service = new PetService
        {
            ProviderId = providerId,
            Name = request.Name,
            Description = request.Description,
            BasePrice = request.BasePrice,
            DurationMinutes = request.DurationMinutes
        };

        services[service.Id] = service;

        return Task.FromResult(ToResponse(service));
    }

    public Task<IReadOnlyCollection<ServiceResponse>> ListByProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<ServiceResponse> result = services.Values
            .Where(service => service.ProviderId == providerId)
            .OrderBy(service => service.Name)
            .Select(ToResponse)
            .ToArray();

        return Task.FromResult(result);
    }

    private static ServiceResponse ToResponse(PetService service)
    {
        return new ServiceResponse(
            service.Id,
            service.ProviderId,
            service.Name,
            service.Description,
            service.BasePrice,
            service.DurationMinutes,
            service.IsActive);
    }
}

using System.Collections.Concurrent;
using Pawfront.Application.Services.ProviderServiceLocations;

namespace Pawfront.Infrastructure.Sql.ProviderServices;

internal sealed class InMemoryProviderServiceLocationRegistry : IProviderServiceLocationRegistry
{
    private readonly ConcurrentDictionary<(Guid ProviderId, string ServiceCategory), ProviderServiceLocation> registrations = new();

    public Task<ProviderServiceLocation> SaveAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        var key = (providerId, serviceCategory);
        var now = DateTimeOffset.UtcNow;

        var saved = registrations.AddOrUpdate(
            key,
            _ => new ProviderServiceLocation(
                ProviderServiceRegistrationId: Guid.NewGuid(),
                ProviderId: providerId,
                ServiceCategory: serviceCategory,
                SubCategory: subCategory,
                Latitude: latitude,
                Longitude: longitude,
                CreatedAtUtc: now,
                UpdatedAtUtc: now),
            (_, existing) => existing with
            {
                SubCategory = subCategory,
                Latitude = latitude,
                Longitude = longitude,
                UpdatedAtUtc = now
            });

        return Task.FromResult(saved);
    }
}

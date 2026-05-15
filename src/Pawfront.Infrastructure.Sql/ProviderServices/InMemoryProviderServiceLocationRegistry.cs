using System.Collections.Concurrent;
using Pawfront.Application.Services.ProviderServiceLocations;

namespace Pawfront.Infrastructure.Sql.ProviderServices;

internal sealed class InMemoryProviderServiceLocationRegistry : IProviderServiceLocationRegistry
{
    // Keyed by ProviderId alone: a provider can only have ONE registration at a time.
    private readonly ConcurrentDictionary<Guid, ProviderServiceLocation> registrations = new();

    public Task EnsureCategoryAvailableAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
    {
        if (registrations.TryGetValue(providerId, out var existing)
            && !string.Equals(existing.ServiceCategory, serviceCategory, StringComparison.Ordinal))
        {
            throw new ProviderServiceCategoryConflictException(
                providerId,
                existing.ServiceCategory,
                serviceCategory);
        }

        return Task.CompletedTask;
    }

    public Task<ProviderServiceLocation?> GetByProviderIdAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        registrations.TryGetValue(providerId, out var existing);
        return Task.FromResult<ProviderServiceLocation?>(existing);
    }

    public Task<ProviderServiceLocation> SaveAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var saved = registrations.AddOrUpdate(
            providerId,
            _ => new ProviderServiceLocation(
                ProviderServiceRegistrationId: Guid.NewGuid(),
                ProviderId: providerId,
                ServiceCategory: serviceCategory,
                SubCategory: subCategory,
                Latitude: latitude,
                Longitude: longitude,
                CreatedAtUtc: now,
                UpdatedAtUtc: now),
            (_, existing) =>
            {
                if (!string.Equals(existing.ServiceCategory, serviceCategory, StringComparison.Ordinal))
                {
                    throw new ProviderServiceCategoryConflictException(
                        providerId,
                        existing.ServiceCategory,
                        serviceCategory);
                }

                return existing with
                {
                    SubCategory = subCategory,
                    Latitude = latitude,
                    Longitude = longitude,
                    UpdatedAtUtc = now
                };
            });

        return Task.FromResult(saved);
    }
}

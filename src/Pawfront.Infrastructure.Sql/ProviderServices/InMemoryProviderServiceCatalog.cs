using System.Collections.Concurrent;
using Pawfront.Application.ProviderServices;

namespace Pawfront.Infrastructure.Sql.ProviderServices;

internal sealed class InMemoryProviderServiceCatalog : IProviderServiceCatalog
{
    // Keyed by ServiceId. The (ProviderId, ServiceType) uniqueness is enforced in code.
    private readonly ConcurrentDictionary<Guid, ProviderService> rows = new();

    public Task<ProviderService> UpsertAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        string serviceType,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = rows.Values.FirstOrDefault(
            r => r.ProviderId == providerId
                 && string.Equals(r.ServiceType, serviceType, StringComparison.Ordinal));

        if (existing is null)
        {
            var created = new ProviderService(
                ServiceId: Guid.NewGuid(),
                ProviderId: providerId,
                ServiceCategory: serviceCategory,
                SubCategory: subCategory,
                ServiceType: serviceType,
                IsActive: true,
                CreatedAtUtc: now,
                UpdatedAtUtc: now);
            rows[created.ServiceId] = created;
            return Task.FromResult(created);
        }

        var updated = existing with
        {
            ServiceCategory = serviceCategory,
            SubCategory = subCategory,
            IsActive = true,
            UpdatedAtUtc = now
        };
        rows[existing.ServiceId] = updated;
        return Task.FromResult(updated);
    }

    public Task DeactivateAsync(Guid providerId, string serviceType, CancellationToken cancellationToken)
    {
        var existing = rows.Values.FirstOrDefault(
            r => r.ProviderId == providerId
                 && string.Equals(r.ServiceType, serviceType, StringComparison.Ordinal)
                 && r.IsActive);

        if (existing is not null)
        {
            rows[existing.ServiceId] = existing with
            {
                IsActive = false,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderService>> ListByProviderAsync(
        Guid providerId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderService> filtered = rows.Values
            .Where(r => r.ProviderId == providerId && (includeInactive || r.IsActive))
            .OrderBy(r => r.ServiceType, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(filtered);
    }

    public Task<ProviderService?> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        rows.TryGetValue(serviceId, out var existing);
        return Task.FromResult<ProviderService?>(existing);
    }
}

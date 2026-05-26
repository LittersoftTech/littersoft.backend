namespace Pawfront.Application.ProviderServices;

/// <summary>
/// Catalog of services each provider offers (DayCare, NightStay, GroomingSession,
/// TrainingSession, VetAppointment). Backed by <c>Provider.ProviderServices</c>.
/// ServiceIds minted here are the keys closures, bookings, and slot queries reference.
/// </summary>
public interface IProviderServiceCatalog
{
    /// <summary>
    /// Idempotent upsert keyed by <c>(providerId, serviceType)</c>. Reactivates a
    /// soft-deactivated row if found. Called by offering save flows.
    /// </summary>
    Task<ProviderService> UpsertAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        string serviceType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deactivate (IsActive=0). Called when a provider removes a sub-offering
    /// (e.g. DayCare) on a subsequent offering save. The row is kept so historical
    /// closures/bookings referencing this ServiceId remain readable.
    /// </summary>
    Task DeactivateAsync(Guid providerId, string serviceType, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderService>> ListByProviderAsync(
        Guid providerId,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<ProviderService?> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken);
}

public sealed record ProviderService(
    Guid ServiceId,
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProviderServiceNotFoundException(Guid serviceId)
    : Exception($"Provider service '{serviceId}' was not found.");

public sealed class ProviderServiceNotOwnedByProviderException(Guid serviceId, Guid providerId)
    : Exception($"Provider service '{serviceId}' does not belong to provider '{providerId}'.");

public sealed class ProviderServiceInactiveException(Guid serviceId)
    : Exception($"Provider service '{serviceId}' has been deactivated.");

public sealed class ProviderServiceCatalogProviderNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' was not found.");

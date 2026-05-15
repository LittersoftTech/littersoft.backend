namespace Pawfront.Application.Services.ProviderServiceLocations;

public interface IProviderServiceLocationRegistry
{
    /// <summary>
    /// Throws <see cref="ProviderServiceCategoryConflictException"/> when the provider is already
    /// registered under a different service category. Returns silently when the provider has no
    /// existing registration, or one for the same category. Call this BEFORE any Cosmos writes
    /// during basic registration to avoid orphaning per-category Cosmos documents.
    /// </summary>
    Task EnsureCategoryAvailableAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken);

    Task<ProviderServiceLocation> SaveAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the provider's single service registration, or null if they have not registered yet.
    /// </summary>
    Task<ProviderServiceLocation?> GetByProviderIdAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

public sealed record ProviderServiceLocation(
    Guid ProviderServiceRegistrationId,
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProviderServiceLocationProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

public sealed class ProviderServiceCategoryConflictException(
    Guid providerId,
    string existingCategory,
    string requestedCategory)
    : Exception(
        $"Provider '{providerId}' is already registered under '{existingCategory}' " +
        $"and cannot register under '{requestedCategory}'.")
{
    public string ExistingCategory { get; } = existingCategory;
    public string RequestedCategory { get; } = requestedCategory;
}

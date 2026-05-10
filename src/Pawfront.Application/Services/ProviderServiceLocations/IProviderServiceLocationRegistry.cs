namespace Pawfront.Application.Services.ProviderServiceLocations;

public interface IProviderServiceLocationRegistry
{
    Task<ProviderServiceLocation> SaveAsync(
        Guid providerId,
        string serviceCategory,
        string subCategory,
        decimal latitude,
        decimal longitude,
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

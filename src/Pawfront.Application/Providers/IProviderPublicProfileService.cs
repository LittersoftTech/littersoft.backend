namespace Pawfront.Application.Providers;

public interface IProviderPublicProfileService
{
    /// <summary>
    /// Returns the composite parent-facing view of the provider. Throws
    /// <see cref="ProviderPublicProfileNotFoundException"/> when the provider
    /// has no service registration row (i.e. they finished basic profile
    /// but haven't registered a service yet, or the id doesn't exist).
    /// </summary>
    Task<ProviderPublicProfile> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Services.PetGroomer;

public interface IPetGroomerServiceRegistry
{
    Task<PetGroomerServiceResult> RegisterGroomerShopAsync(
        RegisterGroomerShopCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> RegisterFreelanceGroomerAsync(
        RegisterFreelanceGroomerCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> SaveGroomerShopOfferingAsync(
        SaveGroomerShopOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult> SaveFreelanceGroomerOfferingAsync(
        SaveFreelanceGroomerOfferingCommand command,
        CancellationToken cancellationToken);

    Task<PetGroomerServiceResult?> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the server-defined canonical list of grooming services a provider
    /// can offer (code + display name). Embedded in the GET response so the
    /// mobile picker can render the menu alongside the provider's chosen items.
    /// </summary>
    IReadOnlyList<GroomingServiceCatalogEntry> GetServiceCatalog();
}

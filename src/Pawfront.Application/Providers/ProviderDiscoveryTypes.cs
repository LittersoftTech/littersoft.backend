namespace Pawfront.Application.Providers;

/// <summary>
/// Optional filters for the parent-facing
/// <see cref="IProviderDiscoveryService.ListAsync"/> call.
/// </summary>
/// <param name="ServiceCategory">
/// One of <c>PetSitter | PetGroomer | PetTrainer | PetAdoptionAndSale | Vet</c>,
/// or null to query every category.
/// </param>
/// <param name="Animals">
/// Match providers whose offering handles ANY of the requested animals
/// (OR semantics). Null/empty means "no animal filter". PetAdoptionAndSale
/// providers have no offering and therefore no animal data — they're
/// excluded when an animal filter is set.
/// </param>
/// <param name="City">
/// Case-insensitive exact match on the city stored in the provider's
/// service registration doc. Null means "no city filter".
/// </param>
/// <param name="ServiceLocation">
/// One of <see cref="ProviderServiceLocationFilters"/> (ParentsPlace |
/// ProvidersPlace), or null for "no location filter". Mapped per category
/// onto the offering's stored serviceLocation value(s); a provider whose
/// offering says "Both" matches either. PetAdoptionAndSale providers have
/// no offering — they're excluded when this filter is set.
/// </param>
/// <param name="Skip">Zero-based offset for pagination. Defaults to 0.</param>
/// <param name="Take">Max rows to return. Clamped 1..200 by the endpoint.</param>
public sealed record ProviderDiscoveryFilter(
    string? ServiceCategory,
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    int Skip,
    int Take);

/// <summary>
/// Wire-level values for the parent-facing serviceLocation filter. The
/// per-category mapping onto stored offering values lives in the Cosmos
/// discovery implementation (the stored vocabulary differs per category).
/// </summary>
public static class ProviderServiceLocationFilters
{
    public const string ParentsPlace = "ParentsPlace";
    public const string ProvidersPlace = "ProvidersPlace";
}

/// <summary>
/// Slim per-provider card returned by the discovery endpoint. The mobile
/// client calls <c>GET /providers/{providerId}</c> for the full offering +
/// working hours + time off shape.
/// </summary>
public sealed record ProviderSummary(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    // Business name for shops/hotels/clinics; null for freelance
    // sub-categories (mobile UI renders something like "Freelance Pet Sitter").
    string? DisplayName,
    string? ImageUrl,
    string City,
    // Description for shop/hotel/clinic, AboutYou for freelancers, null when
    // neither is set. Free text; mobile may truncate.
    string? About,
    // The category-specific animals list (AnimalsHandled / PetsTrained /
    // AnimalsTreated), normalised on the way out. Empty when no offering yet.
    IReadOnlyCollection<string> AnimalsHandled);

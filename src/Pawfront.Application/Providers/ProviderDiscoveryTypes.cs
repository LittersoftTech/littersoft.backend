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
/// <param name="DogTemperaments">
/// Match providers whose offering declares it can handle ANY of the requested
/// temperaments (OR semantics, same as <paramref name="Animals"/> — a parent
/// with an anxious dog wants everyone comfortable with anxious dogs). Values
/// come from <c>Pawfront.Domain.Vocabularies.Behaviour</c>. Null/empty means
/// "no temperament filter".
///
/// Only PetSitter and PetGroomer offerings carry a <c>dogTemperaments</c> list;
/// the other three categories have no such data, so this filter excludes them
/// entirely — the same posture <paramref name="Animals"/> takes towards
/// PetAdoptionAndSale.
/// </param>
/// <param name="Skip">Zero-based offset for pagination. Defaults to 0.</param>
/// <param name="Take">Max rows to return. Clamped 1..200 by the endpoint.</param>
public sealed record ProviderDiscoveryFilter(
    string? ServiceCategory,
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    int Skip,
    int Take,
    IReadOnlyCollection<string>? DogTemperaments = null);

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
    IReadOnlyCollection<string> AnimalsHandled,
    // The provider's registered street address + zip (from the service doc
    // root). Used by the booking-detail location block; not on the discovery
    // card wire shape.
    string? Address = null,
    string? Zip = null,
    // The dog temperaments this provider says they take. NULL means the CATEGORY
    // records none at all (vet / trainer / adoption-and-sale offerings have no
    // such list), which is a different statement from an EMPTY list — a pet
    // sitter who simply never filled it in. The distinction is what lets a card
    // report "we cannot answer that" rather than "no".
    IReadOnlyCollection<string>? DogTemperaments = null);

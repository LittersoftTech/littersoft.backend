namespace Pawfront.Contracts.Services.PetGroomer;

public sealed record RegisterGroomerShopRequest(
    string GroomerShopName,
    string Address,
    string Zip,
    string City,
    string? TelephoneCountryCode,
    string? TelephoneNumber,
    string Email,
    string? Website,
    string? Description,
    string? ShopImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterFreelanceGroomerRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string? AboutYou,
    string? ProfileImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record SaveGroomerShopOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    GroomingOfferingRequest Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record SaveFreelanceGroomerOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    GroomingOfferingRequest Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record GroomingOfferingRequest(
    IReadOnlyCollection<GroomingServiceItemRequest> Services,
    IReadOnlyCollection<string> AddOns,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

/// <param name="Description">
/// Optional — the groomer's own words about this menu item, shown to the parent
/// when they pick a service to book. Max 500 characters; blank/omitted stores as
/// absent and reads back null. The offering save replaces the whole services
/// array, so resend it with every save (same as price and duration).
/// </param>
public sealed record GroomingServiceItemRequest(
    string Code,
    decimal Price,
    int DurationMinutes,
    bool IsActive,
    // Last + defaulted on purpose: it keeps the field out of the OpenAPI
    // `required` list, so existing clients that omit it stay valid.
    string? Description = null);

public sealed record PetGroomerServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    GroomerShopResponse? GroomerShop,
    FreelanceGroomerResponse? Freelance,
    IReadOnlyCollection<GroomingServiceCatalogEntryResponse> ServiceCatalog,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record GroomingServiceCatalogEntryResponse(
    string Code,
    string DisplayName);

public sealed record GroomerShopResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetGroomerLicenseResponse? License,
    PetGroomerOfferingResponse? Offering);

public sealed record FreelanceGroomerResponse(
    string AboutYou,
    string? ImageUrl,
    PetGroomerLicenseResponse? License,
    PetGroomerOfferingResponse? Offering);

public sealed record PetGroomerLicenseResponse(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetGroomerOfferingResponse(
    GroomingOfferingResponse? Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record GroomingOfferingResponse(
    IReadOnlyCollection<GroomingServiceItemResponse> Services,
    IReadOnlyCollection<string> AddOns,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record GroomingServiceItemResponse(
    string Code,
    string? Description,
    decimal Price,
    int DurationMinutes,
    bool IsActive);

namespace Pawfront.Contracts.Services.PetGroomer;

public sealed record RegisterGroomerShopRequest(
    string GroomerShopName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ShopImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterFreelanceGroomerRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
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
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record PetGroomerServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    GroomerShopResponse? GroomerShop,
    FreelanceGroomerResponse? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

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
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

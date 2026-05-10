namespace Pawfront.Application.Services.PetGroomer;

public sealed record RegisterGroomerShopCommand(
    Guid ProviderId,
    string GroomerShopName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ShopImageUrl);

public sealed record RegisterFreelanceGroomerCommand(
    Guid ProviderId,
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl);

public sealed record SaveGroomerShopOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    GroomingOfferingInput Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record SaveFreelanceGroomerOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    GroomingOfferingInput Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record GroomingOfferingInput(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record PetGroomerServiceResult(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    GroomerShopResult? GroomerShop,
    FreelanceGroomerResult? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record GroomerShopResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetGroomerLicenseResult? License,
    PetGroomerOfferingResult? Offering);

public sealed record FreelanceGroomerResult(
    string AboutYou,
    string? ImageUrl,
    PetGroomerLicenseResult? License,
    PetGroomerOfferingResult? Offering);

public sealed record PetGroomerLicenseResult(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetGroomerOfferingResult(
    GroomingOfferingResult? Session,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation);

public sealed record GroomingOfferingResult(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed class GroomerShopNotRegisteredException(Guid providerId)
    : Exception($"Groomer Shop registration was not found for provider '{providerId}'. Complete the basic Groomer Shop registration first.");

public sealed class FreelanceGroomerNotRegisteredException(Guid providerId)
    : Exception($"Freelance Groomer registration was not found for provider '{providerId}'. Complete the basic Freelance Groomer registration first.");

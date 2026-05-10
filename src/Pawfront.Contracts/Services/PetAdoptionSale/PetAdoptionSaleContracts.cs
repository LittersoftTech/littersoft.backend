namespace Pawfront.Contracts.Services.PetAdoptionSale;

public sealed record RegisterPetShelterRequest(
    string PetShelterName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ShelterImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterPetShopRequest(
    string PetShopName,
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

public sealed record RegisterFreelancePetAdoptionSaleRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record PetAdoptionSaleServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    PetShelterResponse? PetShelter,
    PetShopResponse? PetShop,
    FreelancePetAdoptionSaleResponse? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PetShelterResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl);

public sealed record PetShopResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl);

public sealed record FreelancePetAdoptionSaleResponse(
    string AboutYou,
    string? ImageUrl);

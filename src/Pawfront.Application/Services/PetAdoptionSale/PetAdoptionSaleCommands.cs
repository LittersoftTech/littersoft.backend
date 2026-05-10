namespace Pawfront.Application.Services.PetAdoptionSale;

public sealed record RegisterPetShelterCommand(
    Guid ProviderId,
    string PetShelterName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ShelterImageUrl);

public sealed record RegisterPetShopCommand(
    Guid ProviderId,
    string PetShopName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ShopImageUrl);

public sealed record RegisterFreelancePetAdoptionSaleCommand(
    Guid ProviderId,
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl);

public sealed record PetAdoptionSaleServiceResult(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    PetShelterResult? PetShelter,
    PetShopResult? PetShop,
    FreelancePetAdoptionSaleResult? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PetShelterResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl);

public sealed record PetShopResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl);

public sealed record FreelancePetAdoptionSaleResult(
    string AboutYou,
    string? ImageUrl);

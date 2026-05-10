namespace Pawfront.Application.Services.PetSitter;

public sealed record RegisterPetHotelCommand(
    Guid ProviderId,
    string PetHotelName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? HotelImageUrl);

public sealed record RegisterFreelancePetSitterCommand(
    Guid ProviderId,
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl);

public sealed record SavePetHotelOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    BoardingOfferingInput? DayCare,
    BoardingOfferingInput? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record SaveFreelancePetSitterOfferingCommand(
    Guid ProviderId,
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    BoardingOfferingInput? DayCare,
    BoardingOfferingInput? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record BoardingOfferingInput(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record PetSitterServiceResult(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    PetHotelResult? PetHotel,
    FreelancePetSitterResult? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PetHotelResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetSitterLicenseResult? License,
    PetSitterOfferingResult? Offering);

public sealed record FreelancePetSitterResult(
    string AboutYou,
    string? ImageUrl,
    PetSitterLicenseResult? License,
    PetSitterOfferingResult? Offering);

public sealed record PetSitterLicenseResult(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetSitterOfferingResult(
    BoardingOfferingResult? DayCare,
    BoardingOfferingResult? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record BoardingOfferingResult(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed class PetHotelNotRegisteredException(Guid providerId)
    : Exception($"Pet Hotel registration was not found for provider '{providerId}'. Complete the basic Pet Hotel registration first.");

public sealed class FreelancePetSitterNotRegisteredException(Guid providerId)
    : Exception($"Freelance Pet Sitter registration was not found for provider '{providerId}'. Complete the basic Freelance Pet Sitter registration first.");

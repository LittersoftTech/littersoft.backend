namespace Pawfront.Contracts.Services.PetSitter;

public sealed record RegisterPetHotelRequest(
    string PetHotelName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? HotelImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterFreelancePetSitterRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record SavePetHotelOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    BoardingOfferingRequest? DayCare,
    BoardingOfferingRequest? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record SaveFreelancePetSitterOfferingRequest(
    string LicenseNumber,
    string LicenseType,
    string LicenseImageUrl,
    BoardingOfferingRequest? DayCare,
    BoardingOfferingRequest? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record BoardingOfferingRequest(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record PetSitterServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    PetHotelResponse? PetHotel,
    FreelancePetSitterResponse? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PetHotelResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    PetSitterLicenseResponse? License,
    PetSitterOfferingResponse? Offering);

public sealed record FreelancePetSitterResponse(
    string AboutYou,
    string? ImageUrl,
    PetSitterLicenseResponse? License,
    PetSitterOfferingResponse? Offering);

public sealed record PetSitterLicenseResponse(
    string LicenseNumber,
    string LicenseType,
    string ImageUrl);

public sealed record PetSitterOfferingResponse(
    BoardingOfferingResponse? DayCare,
    BoardingOfferingResponse? NightStay,
    IReadOnlyCollection<string> AnimalsHandled,
    int MaxPetsAtOneTime,
    IReadOnlyCollection<string> DogTemperaments,
    string ServiceLocation,
    bool AllowParentFood);

public sealed record BoardingOfferingResponse(
    decimal PricePerHour,
    IReadOnlyCollection<string> AddOns,
    int MinimumBookingHours,
    decimal LatePickupCharges,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime);

public sealed record UploadImageResponse(string Url);

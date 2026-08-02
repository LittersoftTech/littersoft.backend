namespace Pawfront.Contracts.PetParentLookup;

/// <summary>
/// Provider-facing pet-parent profile card, returned by
/// <c>GET /api/v1/pet-parents/{petParentId}/details</c> on the PROVIDER host.
/// The provider app shows this when viewing a customer (e.g. from a booking).
/// <c>Rating</c> is always null for now — the review feature isn't built yet;
/// the field is wired so mobile can bind ahead of time. <c>Age</c> is computed
/// server-side from <c>DateOfBirth</c> (full years, UTC today).
/// </summary>
public sealed record PetParentDetailsResponse(
    Guid PetParentId,
    string? ProfileImageUrl,
    decimal? Rating,
    string Name,
    string Gender,
    int Age,
    DateOnly DateOfBirth,
    PetParentAddressResponse Address,
    string? AboutParent,
    string Email,
    string MobileCountryCode,
    string MobileNumber,
    IReadOnlyList<PetParentPetCardResponse> Pets);

/// <summary>The parent's profile address (where they live / default service location).</summary>
public sealed record PetParentAddressResponse(
    string AddressLine,
    string City,
    string ZipCode,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>
/// Slim pet card embedded in <see cref="PetParentDetailsResponse.Pets"/> —
/// enough for the provider app's pet list; drill into
/// <c>GET /pets/{petId}</c> for the full <see cref="PetDetailsResponse"/>.
/// </summary>
public sealed record PetParentPetCardResponse(
    Guid PetId,
    string Name,
    string PetType,
    string Breed,
    string Gender,
    PetAgeResponse Age,
    string? ProfileImageUrl);

/// <summary>Age split into full years + remaining months (pets are often under a year).</summary>
public sealed record PetAgeResponse(int Years, int Months);

/// <summary>
/// Provider-facing full pet profile, returned by
/// <c>GET /api/v1/pets/{petId}</c> on the PROVIDER host. <c>HealthOfPet</c> is
/// the pet's free-text medical history; <c>VaccinationStatus</c> /
/// <c>SterilizationStatus</c> / <c>Temperament</c> are null until the parent
/// fills them via the parent app. <c>Photos</c> is the gallery (oldest-first),
/// distinct from the single <c>ProfileImageUrl</c>.
/// </summary>
public sealed record PetDetailsResponse(
    Guid PetId,
    Guid PetParentId,
    string? ProfileImageUrl,
    string Name,
    string? MicrochipId,
    string PetType,
    string Gender,
    decimal Weight,
    PetAgeResponse Age,
    DateOnly DateOfBirth,
    string Breed,
    string? AboutPet,
    string? HealthOfPet,
    string? VaccinationStatus,
    string? SterilizationStatus,
    string? VaccinationType,
    string? VaccinationDose,
    string? Prescription,
    string? Temperament,
    IReadOnlyList<string> Photos);

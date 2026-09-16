using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Application.Reviews;
using Pawfront.Contracts.ParentPets;
using Pawfront.Contracts.PetParentLookup;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Provider-facing READ-ONLY lookups of pet-parent and pet profiles — the
/// provider app shows these when viewing a customer or the pet on a job.
/// Reuses the parent host's Application services (<see cref="IParentOnboardingService"/>,
/// <see cref="IParentPetService"/>); no write surface is exposed here.
/// </summary>
internal static class PetParentLookupEndpoints
{
    public static IEndpointRouteBuilder MapPetParentLookupEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/pet-parents/{petParentId:guid}/details", GetPetParentDetails);
        builder.MapGet("/pets/{petId:guid}", GetPetDetails);

        return builder;
    }

    private static async Task<IResult> GetPetParentDetails(
        Guid petParentId,
        IParentOnboardingService onboardingService,
        IParentPetService petService,
        IPetParentRatingReader ratingReader,
        CancellationToken cancellationToken)
    {
        Contracts.ParentOnboarding.PetParentProfileDetailsResponse profile;
        try
        {
            profile = await onboardingService.GetProfileAsync(petParentId, cancellationToken);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }

        var pets = await petService.GetPetsAsync(petParentId, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The parent's rating as given BY providers on completed jobs. Null average
        // with a zero count when nobody has rated them yet.
        var rating = await ratingReader.GetAsync(petParentId, cancellationToken);

        return ApiResults.Ok(new PetParentDetailsResponse(
            profile.PetParentId,
            ProfileImageUrl: profile.ProfilePhotoUrl,
            Rating: rating.AverageRating,
            Name: $"{profile.FirstName} {profile.LastName}".Trim(),
            profile.Gender,
            Age: AgeFrom(profile.DateOfBirth, today).Years,
            profile.DateOfBirth,
            new PetParentAddressResponse(
                profile.AddressLine,
                profile.City,
                profile.ZipCode,
                profile.Latitude,
                profile.Longitude),
            AboutParent: NullIfBlank(profile.Description),
            profile.Email,
            profile.MobileCountryCode,
            profile.MobileNumber,
            pets.Select(pet => ToPetCard(pet, today)).ToArray(),
            RatingCount: rating.RatingCount));
    }

    private static async Task<IResult> GetPetDetails(
        Guid petId,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        var pet = await petService.GetPetAsync(petId, cancellationToken);
        if (pet is null)
        {
            return ApiResults.NotFound("PetNotFound", $"No pet exists with id '{petId}'.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return ApiResults.Ok(new PetDetailsResponse(
            pet.PetId,
            pet.PetParentId,
            ProfileImageUrl: pet.ProfilePhotoUrl,
            Name: pet.PetName,
            pet.MicrochipId,
            pet.PetType,
            pet.Gender,
            pet.Weight,
            AgeFrom(pet.DateOfBirth, today),
            pet.DateOfBirth,
            pet.Breed,
            AboutPet: NullIfBlank(pet.Description),
            HealthOfPet: NullIfBlank(pet.MedicalHistory),
            pet.VaccinationStatus,
            pet.SterilizationStatus,
            pet.VaccinationType,
            pet.VaccinationDose,
            pet.Prescription,
            pet.Temperament,
            pet.Photos.Select(photo => photo.PhotoUrl).ToArray()));
    }

    private static PetParentPetCardResponse ToPetCard(PetParentPetWithPhotosResponse pet, DateOnly today) =>
        new(pet.PetId,
            pet.PetName,
            pet.PetType,
            pet.Breed,
            pet.Gender,
            AgeFrom(pet.DateOfBirth, today),
            pet.ProfilePhotoUrl);

    private static PetAgeResponse AgeFrom(DateOnly dateOfBirth, DateOnly today)
    {
        var months = ((today.Year - dateOfBirth.Year) * 12) + today.Month - dateOfBirth.Month;
        if (today.Day < dateOfBirth.Day)
        {
            months--;
        }

        if (months < 0)
        {
            months = 0;
        }

        return new PetAgeResponse(months / 12, months % 12);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

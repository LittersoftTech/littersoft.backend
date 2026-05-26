using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Contracts.Services.PetSitter;
using Pawfront.Domain.Services;

namespace Pawfront.Api.Endpoints;

internal static class PetSitterEndpoints
{
    private static readonly string CategoryName = ProviderServiceCategory.PetSitter.ToString();

    public static IEndpointRouteBuilder MapPetSitterEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/pet-sitter");
        group.MapProviderImageUploadEndpoints();

        group.MapPost("/pet-hotel", RegisterPetHotel);
        group.MapPost("/freelance", RegisterFreelance);
        group.MapPost("/pet-hotel/offering", SavePetHotelOffering);
        group.MapPost("/freelance/offering", SaveFreelanceOffering);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> RegisterPetHotel(
        Guid providerId,
        RegisterPetHotelRequest request,
        IPetSitterServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterPetHotelAsync(
                new RegisterPetHotelCommand(
                    providerId,
                    request.PetHotelName,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.TelephoneCountryCode,
                    request.TelephoneNumber,
                    request.Email,
                    request.Website,
                    request.Description,
                    request.HotelImageUrl),
                cancellationToken);

            await locationRegistry.SaveAsync(
                providerId,
                CategoryName,
                result.SubCategory,
                request.Latitude,
                request.Longitude,
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (ProviderServiceCategoryConflictException exception)
        {
            return ApiResults.Conflict("ServiceCategoryConflict", exception.Message);
        }
        catch (ProviderServiceLocationProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> RegisterFreelance(
        Guid providerId,
        RegisterFreelancePetSitterRequest request,
        IPetSitterServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterFreelancePetSitterAsync(
                new RegisterFreelancePetSitterCommand(
                    providerId,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.Website,
                    request.AboutYou,
                    request.ProfileImageUrl),
                cancellationToken);

            await locationRegistry.SaveAsync(
                providerId,
                CategoryName,
                result.SubCategory,
                request.Latitude,
                request.Longitude,
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (ProviderServiceCategoryConflictException exception)
        {
            return ApiResults.Conflict("ServiceCategoryConflict", exception.Message);
        }
        catch (ProviderServiceLocationProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SavePetHotelOffering(
        Guid providerId,
        SavePetHotelOfferingRequest request,
        IPetSitterServiceRegistry registry,
        IProviderServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SavePetHotelOfferingAsync(
                new SavePetHotelOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToBoardingInput(request.DayCare),
                    ToBoardingInput(request.NightStay),
                    request.AnimalsHandled,
                    request.MaxPetsAtOneTime,
                    request.DogTemperaments,
                    request.ServiceLocation,
                    request.AllowParentFood),
                cancellationToken);

            await SyncPetSitterServicesAsync(
                providerId, result.SubCategory,
                result.PetHotel?.Offering,
                serviceCatalog, cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (PetHotelNotRegisteredException exception)
        {
            return ApiResults.NotFound("PetHotelNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveFreelanceOffering(
        Guid providerId,
        SaveFreelancePetSitterOfferingRequest request,
        IPetSitterServiceRegistry registry,
        IProviderServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveFreelancePetSitterOfferingAsync(
                new SaveFreelancePetSitterOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToBoardingInput(request.DayCare),
                    ToBoardingInput(request.NightStay),
                    request.AnimalsHandled,
                    request.MaxPetsAtOneTime,
                    request.DogTemperaments,
                    request.ServiceLocation,
                    request.AllowParentFood),
                cancellationToken);

            await SyncPetSitterServicesAsync(
                providerId, result.SubCategory,
                result.Freelance?.Offering,
                serviceCatalog, cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (FreelancePetSitterNotRegisteredException exception)
        {
            return ApiResults.NotFound("FreelancePetSitterNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task SyncPetSitterServicesAsync(
        Guid providerId,
        string subCategory,
        PetSitterOfferingResult? offering,
        IProviderServiceCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (offering?.DayCare is not null)
        {
            await catalog.UpsertAsync(providerId, CategoryName, subCategory,
                ProviderServiceTypes.DayCare, cancellationToken);
        }
        else
        {
            await catalog.DeactivateAsync(providerId, ProviderServiceTypes.DayCare, cancellationToken);
        }

        if (offering?.NightStay is not null)
        {
            await catalog.UpsertAsync(providerId, CategoryName, subCategory,
                ProviderServiceTypes.NightStay, cancellationToken);
        }
        else
        {
            await catalog.DeactivateAsync(providerId, ProviderServiceTypes.NightStay, cancellationToken);
        }
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IPetSitterServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await registry.GetAsync(providerId, cancellationToken);
        return result is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(result));
    }

    private static BoardingOfferingInput? ToBoardingInput(BoardingOfferingRequest? request)
    {
        return request is null
            ? null
            : new BoardingOfferingInput(
                request.PricePerHour,
                request.AddOns,
                request.MinimumBookingHours,
                request.LatePickupCharges,
                request.DropOffTime,
                request.PickUpTime);
    }

    private static PetSitterServiceResponse ToResponse(PetSitterServiceResult result)
    {
        return new PetSitterServiceResponse(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.PetHotel is null
                ? null
                : new PetHotelResponse(
                    result.PetHotel.Name,
                    result.PetHotel.TelephoneCountryCode,
                    result.PetHotel.TelephoneNumber,
                    result.PetHotel.Email,
                    result.PetHotel.Description,
                    result.PetHotel.ImageUrl,
                    ToLicenseResponse(result.PetHotel.License),
                    ToOfferingResponse(result.PetHotel.Offering)),
            result.Freelance is null
                ? null
                : new FreelancePetSitterResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToLicenseResponse(result.Freelance.License),
                    ToOfferingResponse(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }

    private static PetSitterLicenseResponse? ToLicenseResponse(PetSitterLicenseResult? license)
    {
        return license is null
            ? null
            : new PetSitterLicenseResponse(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetSitterOfferingResponse? ToOfferingResponse(PetSitterOfferingResult? offering)
    {
        return offering is null
            ? null
            : new PetSitterOfferingResponse(
                ToBoardingResponse(offering.DayCare),
                ToBoardingResponse(offering.NightStay),
                offering.AnimalsHandled,
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments,
                offering.ServiceLocation,
                offering.AllowParentFood);
    }

    private static BoardingOfferingResponse? ToBoardingResponse(BoardingOfferingResult? offering)
    {
        return offering is null
            ? null
            : new BoardingOfferingResponse(
                offering.PricePerHour,
                offering.AddOns,
                offering.MinimumBookingHours,
                offering.LatePickupCharges,
                offering.DropOffTime,
                offering.PickUpTime);
    }
}

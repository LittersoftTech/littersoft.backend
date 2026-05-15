using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Contracts.Services.PetGroomer;
using Pawfront.Domain.Services;

namespace Pawfront.Api.Endpoints;

internal static class PetGroomerEndpoints
{
    private static readonly string CategoryName = ProviderServiceCategory.PetGroomer.ToString();

    public static IEndpointRouteBuilder MapPetGroomerEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/pet-groomer");
        group.MapProviderImageUploadEndpoints();

        group.MapPost("/groomer-shop", RegisterGroomerShop);
        group.MapPost("/freelance", RegisterFreelance);
        group.MapPost("/groomer-shop/offering", SaveGroomerShopOffering);
        group.MapPost("/freelance/offering", SaveFreelanceOffering);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> RegisterGroomerShop(
        Guid providerId,
        RegisterGroomerShopRequest request,
        IPetGroomerServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterGroomerShopAsync(
                new RegisterGroomerShopCommand(
                    providerId,
                    request.GroomerShopName,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.TelephoneCountryCode,
                    request.TelephoneNumber,
                    request.Email,
                    request.Website,
                    request.Description,
                    request.ShopImageUrl),
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
        RegisterFreelanceGroomerRequest request,
        IPetGroomerServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterFreelanceGroomerAsync(
                new RegisterFreelanceGroomerCommand(
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

    private static async Task<IResult> SaveGroomerShopOffering(
        Guid providerId,
        SaveGroomerShopOfferingRequest request,
        IPetGroomerServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveGroomerShopOfferingAsync(
                new SaveGroomerShopOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToGroomingInput(request.Session),
                    request.AnimalsHandled,
                    request.MaxPetsAtOneTime,
                    request.DogTemperaments,
                    request.ServiceLocation),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (GroomerShopNotRegisteredException exception)
        {
            return ApiResults.NotFound("GroomerShopNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveFreelanceOffering(
        Guid providerId,
        SaveFreelanceGroomerOfferingRequest request,
        IPetGroomerServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveFreelanceGroomerOfferingAsync(
                new SaveFreelanceGroomerOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToGroomingInput(request.Session),
                    request.AnimalsHandled,
                    request.MaxPetsAtOneTime,
                    request.DogTemperaments,
                    request.ServiceLocation),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (FreelanceGroomerNotRegisteredException exception)
        {
            return ApiResults.NotFound("FreelanceGroomerNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IPetGroomerServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await registry.GetAsync(providerId, cancellationToken);
        return result is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(result));
    }

    private static GroomingOfferingInput ToGroomingInput(GroomingOfferingRequest request)
    {
        return new GroomingOfferingInput(
            request.PricePerHour,
            request.AddOns,
            request.MinimumBookingHours,
            request.LatePickupCharges,
            request.DropOffTime,
            request.PickUpTime);
    }

    private static PetGroomerServiceResponse ToResponse(PetGroomerServiceResult result)
    {
        return new PetGroomerServiceResponse(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.GroomerShop is null
                ? null
                : new GroomerShopResponse(
                    result.GroomerShop.Name,
                    result.GroomerShop.TelephoneCountryCode,
                    result.GroomerShop.TelephoneNumber,
                    result.GroomerShop.Email,
                    result.GroomerShop.Description,
                    result.GroomerShop.ImageUrl,
                    ToLicenseResponse(result.GroomerShop.License),
                    ToOfferingResponse(result.GroomerShop.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceGroomerResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToLicenseResponse(result.Freelance.License),
                    ToOfferingResponse(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }

    private static PetGroomerLicenseResponse? ToLicenseResponse(PetGroomerLicenseResult? license)
    {
        return license is null
            ? null
            : new PetGroomerLicenseResponse(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetGroomerOfferingResponse? ToOfferingResponse(PetGroomerOfferingResult? offering)
    {
        return offering is null
            ? null
            : new PetGroomerOfferingResponse(
                ToGroomingResponse(offering.Session),
                offering.AnimalsHandled,
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments,
                offering.ServiceLocation);
    }

    private static GroomingOfferingResponse? ToGroomingResponse(GroomingOfferingResult? offering)
    {
        return offering is null
            ? null
            : new GroomingOfferingResponse(
                offering.PricePerHour,
                offering.AddOns,
                offering.MinimumBookingHours,
                offering.LatePickupCharges,
                offering.DropOffTime,
                offering.PickUpTime);
    }
}

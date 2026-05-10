using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Contracts.Services.PetAdoptionSale;
using Pawfront.Domain.Services;

namespace Pawfront.Api.Endpoints;

internal static class PetAdoptionSaleEndpoints
{
    private static readonly string CategoryName = ProviderServiceCategory.PetAdoptionAndSale.ToString();

    public static IEndpointRouteBuilder MapPetAdoptionSaleEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/pet-adoption-sale");
        group.MapProviderImageUploadEndpoints();

        group.MapPost("/pet-shelter", RegisterPetShelter);
        group.MapPost("/pet-shop", RegisterPetShop);
        group.MapPost("/freelance", RegisterFreelance);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> RegisterPetShelter(
        Guid providerId,
        RegisterPetShelterRequest request,
        IPetAdoptionSaleServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.RegisterPetShelterAsync(
                new RegisterPetShelterCommand(
                    providerId,
                    request.PetShelterName,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.TelephoneCountryCode,
                    request.TelephoneNumber,
                    request.Email,
                    request.Website,
                    request.Description,
                    request.ShelterImageUrl),
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
        catch (ProviderServiceLocationProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> RegisterPetShop(
        Guid providerId,
        RegisterPetShopRequest request,
        IPetAdoptionSaleServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.RegisterPetShopAsync(
                new RegisterPetShopCommand(
                    providerId,
                    request.PetShopName,
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
        RegisterFreelancePetAdoptionSaleRequest request,
        IPetAdoptionSaleServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.RegisterFreelanceAsync(
                new RegisterFreelancePetAdoptionSaleCommand(
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
        catch (ProviderServiceLocationProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IPetAdoptionSaleServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await registry.GetAsync(providerId, cancellationToken);
        return result is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(result));
    }

    private static PetAdoptionSaleServiceResponse ToResponse(PetAdoptionSaleServiceResult result)
    {
        return new PetAdoptionSaleServiceResponse(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.PetShelter is null
                ? null
                : new PetShelterResponse(
                    result.PetShelter.Name,
                    result.PetShelter.TelephoneCountryCode,
                    result.PetShelter.TelephoneNumber,
                    result.PetShelter.Email,
                    result.PetShelter.Description,
                    result.PetShelter.ImageUrl),
            result.PetShop is null
                ? null
                : new PetShopResponse(
                    result.PetShop.Name,
                    result.PetShop.TelephoneCountryCode,
                    result.PetShop.TelephoneNumber,
                    result.PetShop.Email,
                    result.PetShop.Description,
                    result.PetShop.ImageUrl),
            result.Freelance is null
                ? null
                : new FreelancePetAdoptionSaleResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }
}

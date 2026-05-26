using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Contracts.Services.PetTrainer;
using Pawfront.Domain.Services;

namespace Pawfront.Api.Endpoints;

internal static class PetTrainerEndpoints
{
    private static readonly string CategoryName = ProviderServiceCategory.PetTrainer.ToString();

    public static IEndpointRouteBuilder MapPetTrainerEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/pet-trainer");
        group.MapProviderImageUploadEndpoints();

        group.MapPost("/training-school", RegisterTrainingSchool);
        group.MapPost("/freelance", RegisterFreelance);
        group.MapPost("/training-school/offering", SaveTrainingSchoolOffering);
        group.MapPost("/freelance/offering", SaveFreelanceOffering);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> RegisterTrainingSchool(
        Guid providerId,
        RegisterTrainingSchoolRequest request,
        IPetTrainerServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterTrainingSchoolAsync(
                new RegisterTrainingSchoolCommand(
                    providerId,
                    request.TrainingSchoolName,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.TelephoneCountryCode,
                    request.TelephoneNumber,
                    request.Email,
                    request.Website,
                    request.Description,
                    request.SchoolImageUrl),
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
        RegisterFreelanceTrainerRequest request,
        IPetTrainerServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            await locationRegistry.EnsureCategoryAvailableAsync(
                providerId, CategoryName, cancellationToken);

            var result = await registry.RegisterFreelanceTrainerAsync(
                new RegisterFreelanceTrainerCommand(
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

    private static async Task<IResult> SaveTrainingSchoolOffering(
        Guid providerId,
        SaveTrainingSchoolOfferingRequest request,
        IPetTrainerServiceRegistry registry,
        IProviderServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveTrainingSchoolOfferingAsync(
                new SaveTrainingSchoolOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToTrainingSessionInput(request.Session),
                    request.PetsTrained,
                    request.AgeGroups,
                    request.Temperaments,
                    request.MaxConcurrentSessions,
                    request.ServiceLocations,
                    request.TrainingApproaches,
                    request.PreviousExperience,
                    request.PrivateTrainingDescription),
                cancellationToken);

            await SyncTrainerServiceAsync(providerId, result.SubCategory,
                result.TrainingSchool?.Offering, serviceCatalog, cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (TrainingSchoolNotRegisteredException exception)
        {
            return ApiResults.NotFound("TrainingSchoolNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveFreelanceOffering(
        Guid providerId,
        SaveFreelanceTrainerOfferingRequest request,
        IPetTrainerServiceRegistry registry,
        IProviderServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveFreelanceTrainerOfferingAsync(
                new SaveFreelanceTrainerOfferingCommand(
                    providerId,
                    request.LicenseNumber,
                    request.LicenseType,
                    request.LicenseImageUrl,
                    ToTrainingSessionInput(request.Session),
                    request.PetsTrained,
                    request.AgeGroups,
                    request.Temperaments,
                    request.MaxConcurrentSessions,
                    request.ServiceLocations,
                    request.TrainingApproaches,
                    request.PreviousExperience,
                    request.PrivateTrainingDescription),
                cancellationToken);

            await SyncTrainerServiceAsync(providerId, result.SubCategory,
                result.Freelance?.Offering, serviceCatalog, cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (FreelanceTrainerNotRegisteredException exception)
        {
            return ApiResults.NotFound("FreelanceTrainerNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task SyncTrainerServiceAsync(
        Guid providerId,
        string subCategory,
        PetTrainerOfferingResult? offering,
        IProviderServiceCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (offering?.Session is not null)
        {
            await catalog.UpsertAsync(providerId, CategoryName, subCategory,
                ProviderServiceTypes.TrainingSession, cancellationToken);
        }
        else
        {
            await catalog.DeactivateAsync(providerId, ProviderServiceTypes.TrainingSession, cancellationToken);
        }
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IPetTrainerServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await registry.GetAsync(providerId, cancellationToken);
        return result is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(result));
    }

    private static TrainingSessionInput ToTrainingSessionInput(TrainingSessionRequest request)
    {
        return new TrainingSessionInput(request.SessionDurationHours, request.PricePerSession);
    }

    private static PetTrainerServiceResponse ToResponse(PetTrainerServiceResult result)
    {
        return new PetTrainerServiceResponse(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.TrainingSchool is null
                ? null
                : new TrainingSchoolResponse(
                    result.TrainingSchool.Name,
                    result.TrainingSchool.TelephoneCountryCode,
                    result.TrainingSchool.TelephoneNumber,
                    result.TrainingSchool.Email,
                    result.TrainingSchool.Description,
                    result.TrainingSchool.ImageUrl,
                    ToLicenseResponse(result.TrainingSchool.License),
                    ToOfferingResponse(result.TrainingSchool.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceTrainerResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToLicenseResponse(result.Freelance.License),
                    ToOfferingResponse(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }

    private static PetTrainerLicenseResponse? ToLicenseResponse(PetTrainerLicenseResult? license)
    {
        return license is null
            ? null
            : new PetTrainerLicenseResponse(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetTrainerOfferingResponse? ToOfferingResponse(PetTrainerOfferingResult? offering)
    {
        return offering is null
            ? null
            : new PetTrainerOfferingResponse(
                ToTrainingSessionResponse(offering.Session),
                offering.PetsTrained,
                offering.AgeGroups,
                offering.Temperaments,
                offering.MaxConcurrentSessions,
                offering.ServiceLocations,
                offering.TrainingApproaches,
                offering.PreviousExperience,
                offering.PrivateTrainingDescription);
    }

    private static TrainingSessionResponse? ToTrainingSessionResponse(TrainingSessionResult? session)
    {
        return session is null
            ? null
            : new TrainingSessionResponse(session.SessionDurationHours, session.PricePerSession);
    }
}

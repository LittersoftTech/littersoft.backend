using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Application.Services.Vet;
using Pawfront.Contracts.Services.Vet;
using Pawfront.Domain.Services;

namespace Pawfront.Api.Endpoints;

internal static class VetEndpoints
{
    private static readonly string CategoryName = ProviderServiceCategory.Vet.ToString();

    public static IEndpointRouteBuilder MapVetEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/vet");
        group.MapProviderImageUploadEndpoints();

        group.MapPost("/vet-clinic", RegisterVetClinic);
        group.MapPost("/freelance", RegisterFreelance);
        group.MapPost("/vet-clinic/offering", SaveVetClinicOffering);
        group.MapPost("/freelance/offering", SaveFreelanceOffering);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> RegisterVetClinic(
        Guid providerId,
        RegisterVetClinicRequest request,
        IVetServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.RegisterVetClinicAsync(
                new RegisterVetClinicCommand(
                    providerId,
                    request.ClinicName,
                    request.Address,
                    request.Zip,
                    request.City,
                    request.TelephoneCountryCode,
                    request.TelephoneNumber,
                    request.Email,
                    request.Website,
                    request.Description,
                    request.ClinicImageUrl),
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
        RegisterFreelanceVeterinarianRequest request,
        IVetServiceRegistry registry,
        IProviderServiceLocationRegistry locationRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.RegisterFreelanceVeterinarianAsync(
                new RegisterFreelanceVeterinarianCommand(
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

    private static async Task<IResult> SaveVetClinicOffering(
        Guid providerId,
        SaveVetClinicOfferingRequest request,
        IVetServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveVetClinicOfferingAsync(
                new SaveVetClinicOfferingCommand(
                    providerId,
                    request.CertificateImageUrl,
                    ToVetAppointmentInput(request.Appointment),
                    request.AnimalsTreated,
                    request.MaxConcurrentConsultations,
                    request.ServiceLocation),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (VetClinicNotRegisteredException exception)
        {
            return ApiResults.NotFound("VetClinicNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveFreelanceOffering(
        Guid providerId,
        SaveFreelanceVeterinarianOfferingRequest request,
        IVetServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await registry.SaveFreelanceVeterinarianOfferingAsync(
                new SaveFreelanceVeterinarianOfferingCommand(
                    providerId,
                    request.CertificateImageUrl,
                    ToVetAppointmentInput(request.Appointment),
                    request.AnimalsTreated,
                    request.ServiceLocation),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (FreelanceVeterinarianNotRegisteredException exception)
        {
            return ApiResults.NotFound("FreelanceVeterinarianNotRegistered", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IVetServiceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await registry.GetAsync(providerId, cancellationToken);
        return result is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(result));
    }

    private static VetAppointmentInput ToVetAppointmentInput(VetAppointmentRequest request)
    {
        return new VetAppointmentInput(request.AppointmentDurationHours, request.PricePerAppointment);
    }

    private static VetServiceResponse ToResponse(VetServiceResult result)
    {
        return new VetServiceResponse(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.VetClinic is null
                ? null
                : new VetClinicResponse(
                    result.VetClinic.Name,
                    result.VetClinic.TelephoneCountryCode,
                    result.VetClinic.TelephoneNumber,
                    result.VetClinic.Email,
                    result.VetClinic.Description,
                    result.VetClinic.ImageUrl,
                    ToCertificateResponse(result.VetClinic.Certificate),
                    ToOfferingResponse(result.VetClinic.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceVeterinarianResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToCertificateResponse(result.Freelance.Certificate),
                    ToOfferingResponse(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }

    private static VetCertificateResponse? ToCertificateResponse(VetCertificateResult? certificate)
    {
        return certificate is null ? null : new VetCertificateResponse(certificate.ImageUrl);
    }

    private static VetOfferingResponse? ToOfferingResponse(VetOfferingResult? offering)
    {
        return offering is null
            ? null
            : new VetOfferingResponse(
                ToAppointmentResponse(offering.Appointment),
                offering.AnimalsTreated,
                offering.MaxConcurrentConsultations,
                offering.ServiceLocation);
    }

    private static VetAppointmentResponse? ToAppointmentResponse(VetAppointmentResult? appointment)
    {
        return appointment is null
            ? null
            : new VetAppointmentResponse(appointment.AppointmentDurationHours, appointment.PricePerAppointment);
    }
}

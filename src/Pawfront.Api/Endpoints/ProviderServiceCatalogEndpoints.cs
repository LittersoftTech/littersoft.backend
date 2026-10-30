using Pawfront.Application.ProviderServices;
using Pawfront.Contracts.ProviderServices;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// GET /providers/{providerId}/services — lists the services a provider currently
/// offers (DayCare, NightStay, GroomingSession, TrainingSession, VetAppointment).
/// Clients use the ServiceIds returned here when creating closures, bookings, and
/// slot queries.
/// </summary>
internal static class ProviderServiceCatalogEndpoints
{
    public static IEndpointRouteBuilder MapProviderServiceCatalogEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services");
        group.MapGet("/", ListServices);
        group.MapPost("/{serviceId:guid}/deactivate", DeactivateService);
        return builder;
    }

    // Soft-deactivation preserves the offering configuration, booking history,
    // and the provider's independent business online/offline switch.
    private static async Task<IResult> DeactivateService(
        Guid providerId,
        Guid serviceId,
        IProviderServiceCatalog catalog,
        CancellationToken cancellationToken)
    {
        var service = await catalog.GetByIdAsync(serviceId, cancellationToken);
        if (service is null || service.ProviderId != providerId)
        {
            return ApiResults.NotFound("ProviderServiceNotFound", "Service was not found for this provider.");
        }

        await catalog.DeactivateAsync(providerId, service.ServiceType, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListServices(
        Guid providerId,
        bool? includeInactive,
        IProviderServiceCatalog catalog,
        CancellationToken cancellationToken)
    {
        var services = await catalog.ListByProviderAsync(
            providerId, includeInactive ?? false, cancellationToken);

        var response = new ProviderServicesResponse(
            providerId,
            services.Select(s => new ProviderServiceSummary(
                s.ServiceId, s.ProviderId, s.ServiceCategory, s.SubCategory,
                s.ServiceType, s.IsActive, s.CreatedAtUtc, s.UpdatedAtUtc)).ToArray());

        return ApiResults.Ok(response);
    }
}

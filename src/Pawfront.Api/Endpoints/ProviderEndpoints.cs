using Pawfront.Application.Bookings;
using Pawfront.Application.Providers;
using Pawfront.Application.Services;
using Pawfront.Contracts.Bookings;
using Pawfront.Contracts.Providers;
using Pawfront.Contracts.Services;

namespace Pawfront.Api.Endpoints;

internal static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder builder)
    {
        var providers = builder.MapGroup("/providers");

        providers.MapPost("/", CreateProvider);
        providers.MapGet("/", ListProviders);

        providers.MapPost("/{providerId:guid}/services", CreateService);
        providers.MapGet("/{providerId:guid}/services", ListServices);

        providers.MapPost("/{providerId:guid}/bookings", CreateBooking);
        providers.MapGet("/{providerId:guid}/bookings", ListBookings);

        return builder;
    }

    private static async Task<IResult> CreateProvider(
        CreateProviderRequest request,
        IProviderService providerService,
        CancellationToken cancellationToken)
    {
        var provider = await providerService.CreateAsync(request, cancellationToken);
        return ApiResults.Created($"/api/v1/providers/{provider.Id}", provider);
    }

    private static async Task<IResult> ListProviders(
        IProviderService providerService,
        CancellationToken cancellationToken)
    {
        var providerList = await providerService.ListAsync(cancellationToken);
        return ApiResults.Ok(providerList);
    }

    private static async Task<IResult> CreateService(
        Guid providerId,
        CreateServiceRequest request,
        IPetServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        var service = await serviceCatalog.CreateAsync(providerId, request, cancellationToken);
        return ApiResults.Created($"/api/v1/providers/{providerId}/services/{service.Id}", service);
    }

    private static async Task<IResult> ListServices(
        Guid providerId,
        IPetServiceCatalog serviceCatalog,
        CancellationToken cancellationToken)
    {
        var serviceList = await serviceCatalog.ListByProviderAsync(providerId, cancellationToken);
        return ApiResults.Ok(serviceList);
    }

    private static async Task<IResult> CreateBooking(
        Guid providerId,
        CreateBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var booking = await bookingService.CreateAsync(providerId, request, cancellationToken);
        return ApiResults.Created($"/api/v1/providers/{providerId}/bookings/{booking.Id}", booking);
    }

    private static async Task<IResult> ListBookings(
        Guid providerId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var bookingList = await bookingService.ListByProviderAsync(providerId, cancellationToken);
        return ApiResults.Ok(bookingList);
    }
}

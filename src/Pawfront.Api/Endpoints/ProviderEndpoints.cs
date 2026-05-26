using Pawfront.Application.Providers;
using Pawfront.Contracts.Providers;

namespace Pawfront.Api.Endpoints;

internal static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder builder)
    {
        var providers = builder.MapGroup("/providers");

        providers.MapPost("/", CreateProvider);
        providers.MapGet("/", ListProviders);

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
}

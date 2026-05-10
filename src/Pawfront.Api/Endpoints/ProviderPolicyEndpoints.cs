using Pawfront.Application.Policies;
using Pawfront.Contracts.Policies;

namespace Pawfront.Api.Endpoints;

internal static class ProviderPolicyEndpoints
{
    public static IEndpointRouteBuilder MapProviderPolicyEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/policy");

        group.MapPost("/payout-methods", SavePayoutMethods);
        group.MapPost("/cancellation", SaveCancellationPolicy);
        group.MapGet("/", Get);

        return builder;
    }

    private static async Task<IResult> SavePayoutMethods(
        Guid providerId,
        SaveProviderPayoutMethodsRequest request,
        IProviderPolicyService policyService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await policyService.SavePayoutMethodsAsync(
                providerId,
                request.PayoutMethods,
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (ProviderPolicyProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveCancellationPolicy(
        Guid providerId,
        SaveProviderCancellationPolicyRequest request,
        IProviderPolicyService policyService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await policyService.SaveCancellationPolicyAsync(
                providerId,
                request.MinimumHoursBeforeCancellation,
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (ProviderPolicyProviderNotFoundException exception)
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
        IProviderPolicyService policyService,
        CancellationToken cancellationToken)
    {
        var result = await policyService.GetAsync(providerId, cancellationToken);
        return ApiResults.Ok(ToResponse(result));
    }

    private static ProviderPolicyResponse ToResponse(ProviderPolicyResult result)
    {
        return new ProviderPolicyResponse(
            result.ProviderId,
            result.PayoutMethods,
            result.MinimumHoursBeforeCancellation,
            result.PolicyUpdatedAtUtc);
    }
}

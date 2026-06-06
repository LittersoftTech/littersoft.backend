using Pawfront.PetParentApi.Endpoints;

namespace Pawfront.PetParentApi.Auth;

/// <summary>
/// Endpoint filter for routes scoped to <c>/pet-parents/{petParentId:guid}</c>.
/// Resolves the caller's <c>PetParentId</c> from their JWT and rejects the
/// request if the route's id doesn't match. Applied at the MapGroup level so
/// every child route inherits the check.
/// </summary>
internal sealed class OwnedPetParentFilter(
    ICurrentPetParentContext currentPetParent) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.HttpContext.GetRouteValue("petParentId") is not string routeRaw
            || !Guid.TryParse(routeRaw, out var routePetParentId))
        {
            // Route constraint :guid should prevent this, but be defensive.
            return ApiResults.BadRequest("InvalidRequest", "Route is missing petParentId.");
        }

        var callerPetParentId = await currentPetParent
            .GetPetParentIdAsync(context.HttpContext.RequestAborted);

        if (callerPetParentId is null)
        {
            return ApiResults.Forbidden(
                "ParentProfileNotCompleted",
                "Complete the parent profile (POST /api/v1/parent-onboarding/profile) before accessing this resource.");
        }

        if (callerPetParentId.Value != routePetParentId)
        {
            return ApiResults.Forbidden(
                "Forbidden",
                "You can only access your own pet-parent profile.");
        }

        return await next(context);
    }
}

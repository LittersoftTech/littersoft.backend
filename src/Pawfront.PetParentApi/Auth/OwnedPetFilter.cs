using Pawfront.Application.ParentOnboarding;
using Pawfront.PetParentApi.Endpoints;

namespace Pawfront.PetParentApi.Auth;

/// <summary>
/// Endpoint filter for routes scoped to <c>/pets/{petId:guid}</c>. Resolves
/// the caller's <c>PetParentId</c> from their JWT, looks up the pet's owning
/// <c>PetParentId</c>, and rejects requests that don't match. Distinguishes
/// "pet not found" (404) from "pet exists but wrong owner" (403).
/// </summary>
internal sealed class OwnedPetFilter(
    ICurrentPetParentContext currentPetParent,
    IPetParentOwnershipReader ownershipReader) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.HttpContext.GetRouteValue("petId") is not string routeRaw
            || !Guid.TryParse(routeRaw, out var routePetId))
        {
            return ApiResults.BadRequest("InvalidRequest", "Route is missing petId.");
        }

        var ct = context.HttpContext.RequestAborted;

        var callerPetParentId = await currentPetParent.GetPetParentIdAsync(ct);
        if (callerPetParentId is null)
        {
            return ApiResults.Forbidden(
                "ParentProfileNotCompleted",
                "Complete the parent profile (POST /api/v1/parent-onboarding/profile) before accessing this resource.");
        }

        var owningPetParentId = await ownershipReader.GetOwningPetParentIdByPetIdAsync(routePetId, ct);
        if (owningPetParentId is null)
        {
            // Pet doesn't exist at all — surface 404 rather than leaking via
            // 403 (which would imply existence-but-not-ownership).
            return ApiResults.NotFound("PetNotFound", $"Pet '{routePetId}' was not found.");
        }

        if (owningPetParentId.Value != callerPetParentId.Value)
        {
            return ApiResults.Forbidden(
                "Forbidden",
                "You can only access pets belonging to your own profile.");
        }

        return await next(context);
    }
}

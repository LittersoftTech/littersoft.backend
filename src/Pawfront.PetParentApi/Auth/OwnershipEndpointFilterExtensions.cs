namespace Pawfront.PetParentApi.Auth;

internal static class OwnershipEndpointFilterExtensions
{
    /// <summary>
    /// Enforces that the caller's resolved <c>PetParentId</c> matches the
    /// route's <c>{petParentId:guid}</c> segment. The strongly-typed
    /// <c>AddEndpointFilter&lt;TFilter&gt;</c> overload only targets
    /// <see cref="RouteHandlerBuilder"/>; on a <see cref="RouteGroupBuilder"/>
    /// (what <c>MapGroup</c> returns) we use the delegate form and resolve
    /// the filter from DI per request.
    /// </summary>
    public static TBuilder RequireOwnedPetParent<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(static async (context, next) =>
        {
            var filter = context.HttpContext.RequestServices.GetRequiredService<OwnedPetParentFilter>();
            return await filter.InvokeAsync(context, next);
        });
    }

    /// <summary>
    /// Enforces that the route's <c>{petId:guid}</c> segment refers to a pet
    /// owned by the caller's resolved <c>PetParentId</c>. Returns 404 for an
    /// unknown pet, 403 for a wrong-owner pet.
    /// </summary>
    public static TBuilder RequireOwnedPet<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(static async (context, next) =>
        {
            var filter = context.HttpContext.RequestServices.GetRequiredService<OwnedPetFilter>();
            return await filter.InvokeAsync(context, next);
        });
    }
}

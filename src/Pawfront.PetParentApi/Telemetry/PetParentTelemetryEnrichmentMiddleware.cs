using System.Diagnostics;

namespace Pawfront.PetParentApi.Telemetry;

/// <summary>
/// Decorates the current ASP.NET Core request <see cref="Activity"/> with
/// <c>pawfront.pet_parent_id</c> when the route exposes a <c>{petParentId}</c>
/// segment. Mirrors the provider host's enrichment middleware.
/// </summary>
internal sealed class PetParentTelemetryEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (Activity.Current is { } activity)
        {
            if (context.GetRouteValue("petParentId") is string petParentIdRaw
                && Guid.TryParse(petParentIdRaw, out var petParentId))
            {
                activity.SetTag(PetParentTelemetry.TagKeys.PetParentId, petParentId);
            }
        }

        await next(context);
    }
}

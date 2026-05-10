using System.Diagnostics;

namespace Pawfront.Api.Telemetry;

/// <summary>
/// Decorates the current ASP.NET Core request <see cref="Activity"/> (and any
/// child spans) with <c>pawfront.provider_id</c> when the route exposes a
/// <c>{providerId}</c> segment. Makes filtering AppInsights by provider trivial.
/// </summary>
internal sealed class ProviderTelemetryEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (Activity.Current is { } activity)
        {
            if (context.GetRouteValue("providerId") is string providerIdRaw
                && Guid.TryParse(providerIdRaw, out var providerId))
            {
                activity.SetTag(PawfrontTelemetry.TagKeys.ProviderId, providerId);
            }
        }

        await next(context);
    }
}

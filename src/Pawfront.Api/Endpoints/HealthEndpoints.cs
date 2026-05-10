namespace Pawfront.Api.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/health", () => ApiResults.Ok(new
        {
            status = "Healthy",
            service = "Pawfront.Api",
            checkedAt = DateTimeOffset.UtcNow
        }));

        return builder;
    }
}

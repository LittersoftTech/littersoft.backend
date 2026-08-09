namespace Pawfront.ChatApi.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/health", () => ApiResults.Ok(new
        {
            status = "Healthy",
            service = "Pawfront.ChatApi",
            checkedAt = DateTimeOffset.UtcNow
        }));

        return builder;
    }
}

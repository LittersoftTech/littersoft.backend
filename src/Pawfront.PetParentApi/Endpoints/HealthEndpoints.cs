namespace Pawfront.PetParentApi.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/health", () => ApiResults.Ok(new
        {
            status = "Healthy",
            service = "Pawfront.PetParentApi",
            checkedAt = DateTimeOffset.UtcNow
        }));

        return builder;
    }
}

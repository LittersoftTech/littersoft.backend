using Pawfront.Contracts.Metadata;

namespace Pawfront.Api.Endpoints;

internal static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder builder)
    {
        // Static reference vocabularies for the mobile pickers (animals,
        // behaviours). Derived from the domain enums, so there is nothing to
        // persist or cache-bust — it always reflects the deployed vocabulary.
        builder.MapGet("/metadata", () => ApiResults.Ok(MetadataCatalog.Build()));

        return builder;
    }
}

using Pawfront.Contracts.Metadata;

namespace Pawfront.PetParentApi.Endpoints;

internal static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder builder)
    {
        // Static reference vocabularies for the mobile pickers (animals,
        // behaviours). Not ownership-filtered: a parent needs these lists
        // during onboarding, before any profile/pet exists.
        builder.MapGet("/metadata", () => ApiResults.Ok(MetadataCatalog.Build()));

        return builder;
    }
}

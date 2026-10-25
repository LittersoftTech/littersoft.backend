using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Pawfront.Application.Storage;

public static class DocumentationEndpoints
{
    public static IEndpointRouteBuilder MapDocumentationEndpoints(this IEndpointRouteBuilder builder, bool providerHost)
    {
        builder.MapGet("/documents/{category}", async Task<IResult> (
            string category,
            IDocumentationStorage storage,
            CancellationToken cancellationToken) =>
        {
            var fileName = DocumentationCatalog.ResolveFileName(category, providerHost);
            if (fileName is null)
            {
                return Results.NotFound();
            }

            var download = await storage.DownloadDocumentAsync(fileName, cancellationToken);
            return download is null
                ? Results.NotFound()
                : Results.Stream(download.Content, DocumentationCatalog.ContentType(fileName), fileName);
        }).WithTags("Documentation");

        return builder;
    }
}

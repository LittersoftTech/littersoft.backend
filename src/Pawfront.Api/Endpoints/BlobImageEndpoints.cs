using Pawfront.Application.Storage;
using Pawfront.Contracts.Images;

namespace Pawfront.Api.Endpoints;

internal static class BlobImageEndpoints
{
    public static IEndpointRouteBuilder MapBlobImageEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/images/fetch", async (
            FetchImageRequest request,
            IPawfrontBlobStorage blobStorage,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.BlobUrl))
            {
                return ApiResults.BadRequest("InvalidBlobUrl", "blobUrl is required.");
            }

            BlobDownloadResult? download;
            try
            {
                download = await blobStorage.DownloadAsync(request.BlobUrl, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest("InvalidBlobUrl", ex.Message);
            }

            if (download is null)
            {
                return ApiResults.NotFound("ImageNotFound", "No blob exists at the supplied URL.");
            }

            return Results.Stream(
                download.Content,
                contentType: download.ContentType);
        });

        return builder;
    }
}

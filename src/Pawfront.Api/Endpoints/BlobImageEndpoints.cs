using Pawfront.Application.Storage;
using Pawfront.Contracts.Storage;

namespace Pawfront.Api.Endpoints;

internal static class BlobImageEndpoints
{
    public static IEndpointRouteBuilder MapBlobImageEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/blob-images", async (
            GetBlobImageRequest request,
            IPawfrontBlobStorage blobStorage,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.BlobUrl))
            {
                return ApiResults.BadRequest("InvalidRequest", "BlobUrl is required.");
            }

            BlobDownload? download;
            try
            {
                download = await blobStorage.DownloadAsync(request.BlobUrl, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest("InvalidRequest", ex.Message);
            }

            if (download is null)
            {
                return ApiResults.NotFound("BlobNotFound", "The requested blob does not exist.");
            }

            return Results.Stream(download.Content, download.ContentType);
        });

        return builder;
    }
}

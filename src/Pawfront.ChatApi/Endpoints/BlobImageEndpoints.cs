using Pawfront.Application.Storage;
using Pawfront.Contracts.Storage;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Universal image fetch — the third copy of an endpoint both other hosts carry.
/// The blob container is private, so a client cannot GET a stored URL directly;
/// it POSTs the URL here and the server streams the bytes back.
///
/// Duplicated rather than shared because each host authenticates against
/// different Firebase projects, and this one accepts both.
/// </summary>
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
            catch (ArgumentException exception)
            {
                return ApiResults.BadRequest("InvalidRequest", exception.Message);
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

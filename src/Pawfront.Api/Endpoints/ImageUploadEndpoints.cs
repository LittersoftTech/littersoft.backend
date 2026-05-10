using Pawfront.Application.Storage;
using Pawfront.Contracts.Services.PetSitter;

namespace Pawfront.Api.Endpoints;

internal static class ImageUploadEndpoints
{
    public static IEndpointRouteBuilder MapProviderImageUploadEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/profile-image", (
                Guid providerId,
                IFormFile file,
                IPawfrontBlobStorage blobStorage,
                CancellationToken cancellationToken) =>
            UploadAsync(BlobUploadKind.ProfilePhoto, providerId, file, blobStorage, cancellationToken))
            .DisableAntiforgery();

        builder.MapPost("/service-image", (
                Guid providerId,
                IFormFile file,
                IPawfrontBlobStorage blobStorage,
                CancellationToken cancellationToken) =>
            UploadAsync(BlobUploadKind.ServicePhoto, providerId, file, blobStorage, cancellationToken))
            .DisableAntiforgery();

        return builder;
    }

    private static async Task<IResult> UploadAsync(
        BlobUploadKind kind,
        Guid providerId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            kind,
            providerId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        return ApiResults.Ok(new UploadImageResponse(url));
    }
}

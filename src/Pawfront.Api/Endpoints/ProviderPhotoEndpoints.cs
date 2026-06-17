using Pawfront.Application.ProviderPhotos;
using Pawfront.Application.Storage;
using Pawfront.Contracts.ProviderPhotos;

namespace Pawfront.Api.Endpoints;

internal static class ProviderPhotoEndpoints
{
    // Hard upper bound on uploaded image size (3 MB), matching the pet-parent
    // host's photo validation.
    private const long MaxPhotoBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    public static IEndpointRouteBuilder MapProviderPhotoEndpoints(this IEndpointRouteBuilder builder)
    {
        var photos = builder.MapGroup("/providers/{providerId:guid}/photos");
        photos.MapPost("/", UploadPhoto).DisableAntiforgery();
        photos.MapGet("/", ListPhotos);
        photos.MapDelete("/{photoId:guid}", DeletePhoto);

        return builder;
    }

    private static async Task<IResult> UploadPhoto(
        Guid providerId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IProviderPhotoService photoService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.ProviderPhoto,
            providerId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await photoService.AddAsync(providerId, url, cancellationToken);
            return ApiResults.Created(
                $"/api/v1/providers/{providerId}/photos/{response.ProviderPhotoId}",
                response);
        }
        catch (ProviderPhotoProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
    }

    private static async Task<IResult> ListPhotos(
        Guid providerId,
        IProviderPhotoService photoService,
        CancellationToken cancellationToken)
    {
        var photos = await photoService.ListAsync(providerId, cancellationToken);
        return ApiResults.Ok(photos);
    }

    private static async Task<IResult> DeletePhoto(
        Guid providerId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IProviderPhotoService photoService,
        CancellationToken cancellationToken)
    {
        DeleteProviderPhotoResponse response;
        try
        {
            response = await photoService.DeleteAsync(providerId, photoId, cancellationToken);
        }
        catch (ProviderPhotoNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderPhotoNotFound", exception.Message);
        }

        // Best-effort blob cleanup: the SQL row is already gone (the source of
        // truth), so a storage hiccup must not fail the request — the blob is
        // merely orphaned for a future sweep.
        try
        {
            await blobStorage.DeleteAsync(response.PhotoUrl, cancellationToken);
        }
        catch
        {
            // Swallow — see above.
        }

        return ApiResults.Ok(response);
    }

    private static IResult? ValidatePhotoFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        if (file.Length > MaxPhotoBytes)
        {
            return ApiResults.BadRequest(
                "ImageTooLarge",
                $"Photo must be {MaxPhotoBytes / (1024 * 1024)} MB or smaller.");
        }

        var contentType = file.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedPhotoContentTypes.Contains(contentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat",
                "Photo must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }
}

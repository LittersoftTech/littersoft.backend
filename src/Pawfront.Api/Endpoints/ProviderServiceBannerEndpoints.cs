using Pawfront.Application.ProviderServiceBanners;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Providers;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Per-service banner image (provider host). A provider uploads a wide banner for
/// a specific bookable service (ServiceId). Stored in
/// <c>Provider.ProviderServiceBanners</c> (one row per service, upserted); the
/// blob lives under the <c>service-banners/&lt;serviceId&gt;/</c> folder.
/// </summary>
internal static class ProviderServiceBannerEndpoints
{
    // Banners can be larger than profile/gallery photos.
    private const long MaxBannerBytes = 5L * 1024 * 1024;

    private static readonly HashSet<string> AllowedBannerContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    public static IEndpointRouteBuilder MapProviderServiceBannerEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/services/{serviceId:guid}/banner-image");
        group.MapPost("/", UploadBanner).DisableAntiforgery();
        group.MapGet("/", GetBanner);

        return builder;
    }

    private static async Task<IResult> UploadBanner(
        Guid providerId,
        Guid serviceId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IProviderServiceBannerService bannerService,
        CancellationToken cancellationToken)
    {
        var validation = ValidateBannerFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.ServiceBanner,
            serviceId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var result = await bannerService.SaveAsync(providerId, serviceId, url, cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (ProviderServiceBannerServiceNotFoundException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
    }

    private static async Task<IResult> GetBanner(
        Guid providerId,
        Guid serviceId,
        IProviderServiceBannerService bannerService,
        CancellationToken cancellationToken)
    {
        var result = await bannerService.GetAsync(serviceId, cancellationToken);
        if (result is null || result.ProviderId != providerId)
        {
            return ApiResults.NotFound(
                "ServiceBannerNotFound",
                $"No banner image is set for service '{serviceId}'.");
        }

        return ApiResults.Ok(ToResponse(result));
    }

    private static ProviderServiceBannerResponse ToResponse(ProviderServiceBannerResult result) =>
        new(result.ServiceId, result.ProviderId, result.BannerImageUrl, result.CreatedAtUtc, result.UpdatedAtUtc);

    private static IResult? ValidateBannerFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        if (file.Length > MaxBannerBytes)
        {
            return ApiResults.BadRequest(
                "ImageTooLarge",
                $"Banner must be {MaxBannerBytes / (1024 * 1024)} MB or smaller.");
        }

        var contentType = file.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedBannerContentTypes.Contains(contentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat",
                "Banner must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }
}

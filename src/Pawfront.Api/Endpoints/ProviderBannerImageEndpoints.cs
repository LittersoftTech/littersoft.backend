using Pawfront.Application.ProviderBanners;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Providers;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's single provider-level banner image — the wide picture asked
/// for at registration alongside the profile photo, shown on their card in the
/// parent-facing searches. Stored on <c>Provider.Providers.BannerImageUrl</c>;
/// the blob lives under the <c>provider-banners/&lt;providerId&gt;/</c> folder.
/// <para>
/// Distinct from <see cref="ProviderServiceBannerEndpoints"/>, which sets a
/// banner for one specific bookable service (ServiceId) and therefore can only
/// be used after the provider has saved an offering.
/// </para>
/// </summary>
internal static class ProviderBannerImageEndpoints
{
    // Banners can be larger than profile/gallery photos — same cap as the
    // per-service banner.
    private const long MaxBannerBytes = 5L * 1024 * 1024;

    private static readonly HashSet<string> AllowedBannerContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    public static IEndpointRouteBuilder MapProviderBannerImageEndpoints(this IEndpointRouteBuilder builder)
    {
        // Upload only — the URL is read back as `bannerImageUrl` on
        // GET /providers/{providerId}/profile (provider host) and
        // GET /providers/{providerId} (parent host), so a dedicated read
        // endpoint would be redundant.
        var group = builder.MapGroup("/providers/{providerId:guid}/banner-image");
        group.MapPost("/", UploadBanner).DisableAntiforgery();

        return builder;
    }

    private static async Task<IResult> UploadBanner(
        Guid providerId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IProviderBannerImageService bannerService,
        CancellationToken cancellationToken)
    {
        var validation = ValidateBannerFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.ProviderBanner,
            providerId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var result = await bannerService.SaveAsync(providerId, url, cancellationToken);
            return ApiResults.Ok(
                new ProviderBannerImageResponse(result.ProviderId, result.BannerImageUrl, result.UpdatedAtUtc));
        }
        catch (ProviderBannerImageProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
    }

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

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Application.Storage;
using BlobDownloadResult = Pawfront.Application.Storage.BlobDownloadResult;

namespace Pawfront.Infrastructure.Azure.Storage;

internal sealed class AzureBlobStorageService(
    IPawfrontSecretProvider secretProvider,
    IOptions<BlobStorageOptions> blobOptions) : IPawfrontBlobStorage
{
    private readonly BlobStorageOptions options = blobOptions.Value;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private BlobContainerClient? containerClient;

    public async Task<string> UploadAsync(
        BlobUploadKind kind,
        Guid providerId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        var container = await GetContainerAsync(cancellationToken);
        var blobName = BuildBlobName(kind, providerId, fileName);
        var blobClient = container.GetBlobClient(blobName);

        await blobClient.UploadAsync(
            content,
            new BlobHttpHeaders { ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType },
            cancellationToken: cancellationToken);

        return blobClient.Uri.ToString();
    }

    public async Task<BlobDownloadResult?> DownloadAsync(
        string blobUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobUrl))
        {
            throw new ArgumentException("Blob URL is required.", nameof(blobUrl));
        }

        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Blob URL must be an absolute http(s) URI.", nameof(blobUrl));
        }

        var container = await GetContainerAsync(cancellationToken);
        var blobName = ResolveManagedBlobName(uri, container);

        var blobClient = container.GetBlobClient(blobName);

        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = response.Value.Details;
            var contentType = string.IsNullOrWhiteSpace(details.ContentType)
                ? "application/octet-stream"
                : details.ContentType;

            return new BlobDownloadResult(
                response.Value.Content,
                contentType,
                details.ContentLength == 0 ? null : details.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string ResolveManagedBlobName(Uri uri, BlobContainerClient container)
    {
        var containerUri = container.Uri;

        if (!string.Equals(uri.Host, containerUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Blob URL host does not belong to the managed storage account.",
                nameof(uri));
        }

        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        if (segments.Length < 2 ||
            !string.Equals(segments[0], container.Name, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]))
        {
            throw new ArgumentException(
                "Blob URL does not point at the managed container.",
                nameof(uri));
        }

        return Uri.UnescapeDataString(segments[1]);
    }

    private string BuildBlobName(BlobUploadKind kind, Guid providerId, string fileName)
    {
        var folder = kind switch
        {
            BlobUploadKind.ProfilePhoto => options.Folders.ProfilePhotos,
            BlobUploadKind.ServicePhoto => options.Folders.ServicePhotos,
            BlobUploadKind.EventBanner => options.Folders.EventBanners,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported blob upload kind.")
        };

        var extension = Path.GetExtension(fileName);
        var unique = Guid.NewGuid().ToString("N");

        var leaf = string.IsNullOrWhiteSpace(extension) ? unique : $"{unique}{extension}";

        return $"{folder.Trim('/')}/{providerId}/{leaf}";
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken cancellationToken)
    {
        if (containerClient is not null)
        {
            return containerClient;
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (containerClient is not null)
            {
                return containerClient;
            }

            if (string.IsNullOrWhiteSpace(options.Container))
            {
                throw new InvalidOperationException("BlobStorage:Container is required.");
            }

            var connectionString = await secretProvider.GetBlobStorageKeyAsync(cancellationToken);
            var serviceClient = new BlobServiceClient(connectionString);
            var client = serviceClient.GetBlobContainerClient(options.Container);
            await client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            containerClient = client;
            return containerClient;
        }
        finally
        {
            semaphore.Release();
        }
    }
}

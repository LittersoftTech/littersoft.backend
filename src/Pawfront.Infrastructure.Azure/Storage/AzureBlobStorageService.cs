using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Pawfront.Application.Configuration;
using Pawfront.Application.Storage;

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

    private string BuildBlobName(BlobUploadKind kind, Guid providerId, string fileName)
    {
        var folder = kind switch
        {
            BlobUploadKind.ProfilePhoto => options.Folders.ProfilePhotos,
            BlobUploadKind.ServicePhoto => options.Folders.ServicePhotos,
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

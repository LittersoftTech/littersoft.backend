using System.Collections.Concurrent;
using Azure;
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

    // One client per container. There are two today — the media container and the
    // invoices container — and they are cached rather than rebuilt because
    // CreateIfNotExists is a network round trip we only want to pay once per
    // container per process.
    private readonly ConcurrentDictionary<string, BlobContainerClient> containerClients =
        new(StringComparer.OrdinalIgnoreCase);

    private BlobServiceClient? serviceClient;

    public async Task<string> UploadAsync(
        BlobUploadKind kind,
        Guid ownerId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.", nameof(fileName));
        }

        var container = await GetContainerAsync(ResolveContainerName(kind), cancellationToken);
        var blobName = BuildBlobName(kind, ownerId, fileName);
        var blobClient = container.GetBlobClient(blobName);

        await blobClient.UploadAsync(
            content,
            new BlobHttpHeaders { ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType },
            cancellationToken: cancellationToken);

        return blobClient.Uri.ToString();
    }

    public async Task<BlobDownload?> DownloadAsync(string blobUrl, CancellationToken cancellationToken)
    {
        var blobClient = await ResolveBlobClientAsync(blobUrl, cancellationToken);

        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = response.Value.Details;
            var contentType = string.IsNullOrWhiteSpace(details.ContentType)
                ? "application/octet-stream"
                : details.ContentType;
            return new BlobDownload(response.Value.Content, contentType, details.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string blobUrl, CancellationToken cancellationToken)
    {
        var blobClient = await ResolveBlobClientAsync(blobUrl, cancellationToken);

        var response = await blobClient.DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots,
            cancellationToken: cancellationToken);
        return response.Value;
    }

    /// <summary>
    /// Maps a stored blob URL back to a client in one of the CONFIGURED
    /// containers, rejecting anything outside them (SSRF guard — the URL must
    /// live under our own storage account and in a container we own, not an
    /// arbitrary host).
    /// </summary>
    private async Task<BlobClient> ResolveBlobClientAsync(string blobUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobUrl))
        {
            throw new ArgumentException("Blob URL is required.", nameof(blobUrl));
        }

        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Blob URL must be an absolute http(s) URL.", nameof(blobUrl));
        }

        // Try each configured container in turn. A URL that matches none of them
        // is rejected exactly as it was when there was only one — widening the
        // set of containers must not widen what a caller can reach.
        foreach (var containerName in ConfiguredContainers())
        {
            var container = await GetContainerAsync(containerName, cancellationToken);
            var containerUri = container.Uri;

            if (!string.Equals(uri.Host, containerUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var containerPath = containerUri.AbsolutePath.TrimEnd('/') + "/";
            if (!uri.AbsolutePath.StartsWith(containerPath, StringComparison.Ordinal))
            {
                continue;
            }

            var blobName = Uri.UnescapeDataString(uri.AbsolutePath.Substring(containerPath.Length));
            if (string.IsNullOrWhiteSpace(blobName))
            {
                throw new ArgumentException("Blob URL is missing a blob name.", nameof(blobUrl));
            }

            return container.GetBlobClient(blobName);
        }

        throw new ArgumentException(
            "Blob URL does not point at a configured container on this storage account.",
            nameof(blobUrl));
    }

    private IEnumerable<string> ConfiguredContainers()
    {
        if (!string.IsNullOrWhiteSpace(options.Container))
        {
            yield return options.Container;
        }

        if (!string.IsNullOrWhiteSpace(options.InvoiceContainer) &&
            !string.Equals(options.InvoiceContainer, options.Container, StringComparison.OrdinalIgnoreCase))
        {
            yield return options.InvoiceContainer;
        }
    }

    private string ResolveContainerName(BlobUploadKind kind) => kind switch
    {
        BlobUploadKind.Invoice => Required(options.InvoiceContainer, "BlobStorage:InvoiceContainer"),
        _ => Required(options.Container, "BlobStorage:Container")
    };

    private static string Required(string? value, string configurationPath)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{configurationPath} is required.")
            : value;

    private string BuildBlobName(BlobUploadKind kind, Guid ownerId, string fileName)
    {
        var folder = kind switch
        {
            BlobUploadKind.ProfilePhoto => options.Folders.ProfilePhotos,
            BlobUploadKind.ServicePhoto => options.Folders.ServicePhotos,
            BlobUploadKind.EventBanner => options.Folders.EventBanners,
            BlobUploadKind.PetParentProfilePhoto => options.Folders.PetParentProfilePhotos,
            BlobUploadKind.PetPhoto => options.Folders.PetPhotos,
            BlobUploadKind.PetParentIdentity => options.Folders.PetParentIdentities,
            BlobUploadKind.PetParentPhoto => options.Folders.PetParentPhotos,
            BlobUploadKind.ProviderPhoto => options.Folders.ProviderPhotos,
            BlobUploadKind.PetProfilePhoto => options.Folders.PetProfilePhotos,
            BlobUploadKind.BookingEvidence => options.Folders.BookingEvidence,
            BlobUploadKind.ServiceBanner => options.Folders.ServiceBanners,
            BlobUploadKind.ProviderBanner => options.Folders.ProviderBanners,
            BlobUploadKind.ReviewPhoto => options.Folders.ReviewPhotos,
            BlobUploadKind.ChatAttachment => options.Folders.ChatAttachments,
            BlobUploadKind.IncidentPhoto => options.Folders.IncidentPhotos,
            BlobUploadKind.Invoice => options.Folders.Invoices,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported blob upload kind.")
        };

        var extension = Path.GetExtension(fileName);
        var unique = Guid.NewGuid().ToString("N");

        var leaf = string.IsNullOrWhiteSpace(extension) ? unique : $"{unique}{extension}";

        // Invoices carry no folder prefix — their container name already says what
        // they are, so the path is just <BookingId>/<file>.
        var prefix = folder.Trim('/');
        return string.IsNullOrEmpty(prefix)
            ? $"{ownerId}/{leaf}"
            : $"{prefix}/{ownerId}/{leaf}";
    }

    private async Task<BlobContainerClient> GetContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        if (containerClients.TryGetValue(containerName, out var cached))
        {
            return cached;
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (containerClients.TryGetValue(containerName, out cached))
            {
                return cached;
            }

            if (serviceClient is null)
            {
                var connectionString = await secretProvider.GetBlobStorageKeyAsync(cancellationToken);
                serviceClient = new BlobServiceClient(connectionString);
            }

            var client = serviceClient.GetBlobContainerClient(containerName);
            await client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            containerClients[containerName] = client;
            return client;
        }
        finally
        {
            semaphore.Release();
        }
    }
}

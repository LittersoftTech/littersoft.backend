namespace Pawfront.Application.Storage;

public interface IPawfrontBlobStorage
{
    Task<string> UploadAsync(
        BlobUploadKind kind,
        Guid providerId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams a blob from our managed container. Returns <c>null</c> when the
    /// blob does not exist. Throws <see cref="ArgumentException"/> when the URL
    /// is malformed or points outside the managed account/container.
    /// </summary>
    Task<BlobDownloadResult?> DownloadAsync(
        string blobUrl,
        CancellationToken cancellationToken);
}

public sealed record BlobDownloadResult(
    Stream Content,
    string ContentType,
    long? ContentLength);

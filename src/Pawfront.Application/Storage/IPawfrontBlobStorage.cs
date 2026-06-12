namespace Pawfront.Application.Storage;

public interface IPawfrontBlobStorage
{
    /// <summary>
    /// Uploads an image to the configured blob container under the folder that
    /// corresponds to <paramref name="kind"/>. <paramref name="ownerId"/> is
    /// the resource the blob belongs to (a ProviderId for provider photos,
    /// a PetParentId for pet-parent photos, etc.); it is used purely as a
    /// folder segment in the blob name and has no FK enforcement.
    /// </summary>
    Task<string> UploadAsync(
        BlobUploadKind kind,
        Guid ownerId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    Task<BlobDownload?> DownloadAsync(
        string blobUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the blob the URL points at. Returns true when a blob was
    /// deleted, false when it didn't exist. Throws
    /// <see cref="ArgumentException"/> for URLs that don't resolve inside
    /// the configured container (same guard as <see cref="DownloadAsync"/>).
    /// </summary>
    Task<bool> DeleteAsync(
        string blobUrl,
        CancellationToken cancellationToken);
}

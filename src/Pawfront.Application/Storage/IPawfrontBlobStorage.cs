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
}

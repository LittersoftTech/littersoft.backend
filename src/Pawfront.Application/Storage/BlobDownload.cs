namespace Pawfront.Application.Storage;

public sealed record BlobDownload(Stream Content, string ContentType, long? ContentLength);

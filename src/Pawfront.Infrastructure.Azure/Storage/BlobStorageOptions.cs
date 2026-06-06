namespace Pawfront.Infrastructure.Azure.Storage;

public sealed class BlobStorageOptions
{
    public string ConnectionStringSecretName { get; init; } = "BlobStorageKey";
    public string Container { get; init; } = "provider-images";
    public BlobStorageFolderOptions Folders { get; init; } = new();
}

public sealed class BlobStorageFolderOptions
{
    public string ProfilePhotos { get; init; } = "profile-photos";
    public string ServicePhotos { get; init; } = "service-photos";
    public string EventBanners { get; init; } = "events";
    public string PetParentProfilePhotos { get; init; } = "pet-parent-profile-photos";
    public string PetPhotos { get; init; } = "pet-photos";
    public string PetParentIdentities { get; init; } = "pet-parent-identities";
}

namespace Pawfront.Application.Storage;

public enum BlobUploadKind
{
    ProfilePhoto = 0,
    ServicePhoto = 1,
    EventBanner = 2,
    PetParentProfilePhoto = 3,
    PetPhoto = 4,
    PetParentIdentity = 5,
    PetParentPhoto = 6,
    ProviderPhoto = 7,
    PetProfilePhoto = 8,
    BookingEvidence = 9,
    // A provider's per-service banner image (owner = ServiceId).
    ServiceBanner = 10
}

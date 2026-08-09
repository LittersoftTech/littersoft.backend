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
    ServiceBanner = 10,
    // A provider's single provider-level banner image (owner = ProviderId),
    // captured at registration and shown on their search card.
    ProviderBanner = 11,
    // A photo a pet parent attached to their review of a booking
    // (owner = BookingReviewId). The review row has to exist first, which is why
    // review photos are a second call rather than part of the submit.
    ReviewPhoto = 12,
    // An image sent in a chat message (owner = ConversationId). Uploaded BEFORE
    // the message, unlike review photos: the message carries the resulting url,
    // so the conversation — which already exists — is what keys the path.
    ChatAttachment = 13
}

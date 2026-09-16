namespace Pawfront.Infrastructure.Azure.Storage;

public sealed class BlobStorageOptions
{
    public string ConnectionStringSecretName { get; init; } = "BlobStorageKey";

    /// <summary>
    /// The default container — every image kind lives here. Named
    /// "provider-images" for legacy reasons; it also holds pet-parent, pet, chat
    /// and support media.
    /// </summary>
    public string Container { get; init; } = "provider-images";

    /// <summary>
    /// Invoice PDFs live in their OWN container, not under <see cref="Container"/>.
    /// They are financial documents rather than media: different retention, a
    /// different content type, and a blast radius worth keeping separate from a
    /// container every photo endpoint can write to. Blobs are laid out as
    /// <c>&lt;BookingId&gt;/&lt;file&gt;.pdf</c> — the booking is the folder, so a
    /// job's parent and provider invoices sit side by side.
    /// </summary>
    public string InvoiceContainer { get; init; } = "invoices";

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
    public string PetParentPhotos { get; init; } = "pet-parent-photos";
    public string ProviderPhotos { get; init; } = "provider-photos";
    public string PetProfilePhotos { get; init; } = "pet-profile-photos";
    public string BookingEvidence { get; init; } = "booking-evidence";
    public string ServiceBanners { get; init; } = "service-banners";
    public string ProviderBanners { get; init; } = "provider-banners";
    public string ReviewPhotos { get; init; } = "review-photos";
    public string ChatAttachments { get; init; } = "chat-attachments";
    public string IncidentPhotos { get; init; } = "incident-photos";

    /// <summary>
    /// Empty on purpose: an invoice's path is <c>&lt;BookingId&gt;/&lt;file&gt;</c>
    /// with no folder prefix, because it lives in its own container and the
    /// container name already says what it holds.
    /// </summary>
    public string Invoices { get; init; } = string.Empty;
}

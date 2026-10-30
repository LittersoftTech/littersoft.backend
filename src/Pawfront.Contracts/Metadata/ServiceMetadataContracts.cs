namespace Pawfront.Contracts.Metadata;

public sealed record ServiceMetadataItem(
    string ServiceName,
    string ServiceOwner,
    string ServiceDescription);

/// <summary>Shared service reference data for both API hosts.</summary>
public static class ServiceMetadataCatalog
{
    private static readonly IReadOnlyList<ServiceMetadataItem> Entries = Array.AsReadOnly(new ServiceMetadataItem[]
    {
        new("Day Care", "Pet Sitter", "Pet day care facility for Short stays up to 12 hrs."),
        new("Night Stay", "Pet Sitter", "Pet night stay facility for longer overnight stays"),
        new("Private Pet Training", "Pet Trainer", "Private pet training tailored to meet customer and pet needs"),
        new("Vet Consultation", "Vet", "Routine check-ups, basic assessments, and medical advice to support your pet’s overall health"),
        new("Bath & Blow Dry", "Pet Groomer", "A basic service where the pet is washed with shampoo, dried with a towel or dryer and lightly brushed"),
        new("Puppy First Groom", "Pet Groomer", "Gentle grooming session to help puppies get used to the grooming process. Includes bath, dry, brush"),
        new("Breed-Specific Styling", "Pet Groomer", "Bespoke haircuts for specific breeds or personal preference (e.g. Poodle Trim, Schnauzer Cut, etc.)"),
        new("Bath, Dry & Brush", "Pet Groomer", "Quick grooming with bath, towel dry, light brush, and hygiene check. Ideal for regular maintenance"),
        new("Tick & Flea Removal", "Pet Groomer", "Manual removal of ticks or fleas to treat infestations, followed by a bath using medicated shampoo"),
        new("Medicated Bath", "Pet Groomer", "Bath using vet-recommended medicated shampoo to treat skin disorders like dermatitis or allergies"),
        new("De-matting", "Pet Groomer", "Careful detangling and removal of mats in the fur using tools or clippers. May require sedation"),
        new("De-Shedding", "Pet Groomer", "Brushing and bathing routine to remove loose fur and undercoat. Great for double coated breeds"),
        new("Wire Coat Hand Stripping", "Pet Groomer", "Labor-intensive grooming for wire hair coat breeds that involves plucking dead hair by hand"),
        new("Cat Grooming", "Pet Groomer", "Specialised grooming for cats including brushing, bath, dry and trimming or de-matting"),
        new("Coat Dyeing", "Pet Groomer", "Use of pet-safe colors or temporary chalks to create stylish or themed designs on fur"),
        new("Nails Clipping", "Pet Groomer", "Trimming nails using safe clippers or grinders to prevent overgrowth and injuries"),
        new("Ears Cleaning", "Pet Groomer", "Gentle cleaning of ears to remove wax and dirt, reducing the risk of infections"),
        new("Oral Hygiene Pack", "Pet Groomer", "Brushing with pet-safe toothpaste or oral gels to reduce tartar and bad breath"),
        new("Anal Gland Expression", "Pet Groomer", "Manual emptying of the anal glands, which helps prevent infections and odor"),
        new("Paw Pad Trimming & Care", "Pet Groomer", "Trimming fur between the paw pads and applying balm for softness and grip"),
        new("Summer Coat Preparation", "Pet Groomer", "Light trim or shave to prepare your pet’s coat for summer"),
        new("Winter Coat Preparation", "Pet Groomer", "Fluff up your pet’s coat to prepare them for winter"),
    });

    /// <summary>
    /// Returns all services when the name is blank, otherwise an exact,
    /// case-insensitive match after trimming surrounding whitespace.
    /// SR is the display name, not a provider service ID or offering code.
    /// </summary>
    public static IReadOnlyList<ServiceMetadataItem> Build(string? serviceName = null) =>
        string.IsNullOrWhiteSpace(serviceName)
            ? Entries
            : Entries.Where(entry => string.Equals(
                entry.ServiceName, serviceName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
}

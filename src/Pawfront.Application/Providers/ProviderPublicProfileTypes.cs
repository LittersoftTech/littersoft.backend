using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;

namespace Pawfront.Application.Providers;

/// <summary>
/// Composite parent-facing view of a registered provider. Built by
/// <see cref="IProviderPublicProfileService"/> in a single round of fan-out:
/// the registration row determines the category, then we fetch the matching
/// category offering, weekly availability, and future closures.
///
/// Exactly one of the per-category fields is non-null, matching
/// <see cref="ServiceCategory"/>. Provider personal info (name, mobile,
/// DOB) is intentionally NOT included — parents see business-facing data,
/// not provider PII.
/// </summary>
public sealed record ProviderPublicProfile(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<DayAvailabilityResult> WorkingHours,
    IReadOnlyList<ProviderClosure> TimeOff,
    // Booking policy the provider advertises (read from the provider-policy
    // store). MinimumHoursBeforeCancellation is null when no policy is set;
    // AcceptedPaymentMethods is the Cash/Digital payout set (empty when none).
    int? MinimumHoursBeforeCancellation,
    IReadOnlyCollection<string> AcceptedPaymentMethods,
    // The provider's profile/business photo — the image they uploaded for their
    // offering. Null when none set.
    string? ProfilePhotoUrl,
    // The provider's gallery photos (Provider.ProviderPhotos), oldest-first.
    // Empty when none.
    IReadOnlyList<string> GalleryImages,
    PetSitterServiceResult? PetSitter,
    PetGroomerServiceResult? PetGroomer,
    PetTrainerServiceResult? PetTrainer,
    PetAdoptionSaleServiceResult? PetAdoptionSale,
    VetServiceResult? Vet,
    // For PetGroomer the catalog of 18 canonical grooming services is
    // embedded so the mobile client can render the menu without a second
    // fetch (matches the provider host's behaviour).
    IReadOnlyList<GroomingServiceCatalogEntry>? GroomingServiceCatalog);

public sealed class ProviderPublicProfileNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' is not registered.");

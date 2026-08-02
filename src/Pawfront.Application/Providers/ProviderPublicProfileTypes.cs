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
/// <see cref="ServiceCategory"/>. Provider personal info (name, DOB) is
/// intentionally NOT included — parents see business-facing data, not provider
/// PII. The exception is <see cref="Email"/> / <see cref="MobileNumber"/>, which
/// are how a parent contacts the provider: for a business those come from the
/// offering, and for a freelancer — who registers no separate business e-mail or
/// telephone — from their own account.
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
    // Bookings this provider has already served (the booking window has ended
    // and neither party cancelled), across all of their services. Same figure
    // the booking-search cards surface.
    int CompletedBookings,
    // The freelancer's "about you" text — who the provider is. Null for
    // shop/clinic/school/shelter sub-categories (they describe the business
    // instead, see ServicesDescription).
    string? Description,
    // The business branch's description (shop/hotel/clinic/school/shelter/
    // pet-shop) — what services the business offers. Null for freelancers.
    string? ServicesDescription,
    // What the provider says about the bookable SERVICE itself (as opposed to
    // who they are / what the business is). PetTrainer only — its offering's
    // PrivateTrainingDescription, lifted here for both sub-categories so mobile
    // doesn't dig into the category branch. Null elsewhere: a groomer's blurbs
    // are per menu item (PetGroomer...offering.session.services[].description),
    // and the remaining categories have no per-service text.
    string? ServiceDescription,
    // Contact details, lifted to the top level so mobile doesn't have to dig into
    // the nested category branch (and finds nothing there for a freelancer).
    // Business sub-categories report the e-mail/telephone captured at
    // registration; freelancers — and businesses that left the optional telephone
    // blank — fall back to the provider's own account contact. Null when neither
    // source has a value.
    string? Email,
    string? MobileCountryCode,
    string? MobileNumber,
    // The provider's profile/business photo — the image they uploaded for their
    // offering. Null when none set.
    string? ProfilePhotoUrl,
    // The provider-level banner (Provider.Providers.BannerImageUrl) — the wide
    // picture uploaded at registration and shown on their search card. Null
    // until uploaded.
    string? BannerImageUrl,
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

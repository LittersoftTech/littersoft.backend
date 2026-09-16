using Pawfront.Contracts.Reviews;
using Pawfront.Contracts.Services.PetAdoptionSale;
using Pawfront.Contracts.Services.PetGroomer;
using Pawfront.Contracts.Services.PetSitter;
using Pawfront.Contracts.Services.PetTrainer;
using Pawfront.Contracts.Services.Vet;

namespace Pawfront.Contracts.Providers;

/// <summary>
/// Parent-facing public profile of a provider — returned by
/// <c>GET /api/v1/providers/{providerId}</c> on the pet-parent host.
/// Exactly one of the per-category fields is populated, matching
/// <see cref="ServiceCategory"/>. Provider personal info (name, DOB) is
/// intentionally omitted; the contact fields are the exception, since a parent
/// needs a way to reach the provider.
/// </summary>
public sealed record ProviderPublicProfileResponse(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<ProviderWorkingHoursDayResponse> WorkingHours,
    IReadOnlyList<ProviderTimeOffEntryResponse> TimeOff,
    // Booking policy the provider advertises. MinimumHoursBeforeCancellation
    // is null when the provider has set no cancellation policy.
    // AcceptedPaymentMethods is the provider's payout-method set (Cash /
    // Digital); empty when none configured.
    int? MinimumHoursBeforeCancellation,
    IReadOnlyCollection<string> AcceptedPaymentMethods,
    // Bookings this provider has already served (the booking window has ended
    // and neither party cancelled), across all of their services.
    int CompletedBookings,
    // The freelancer's "about you" text — who the provider is. Null for
    // shop/clinic/school/shelter sub-categories (see ServicesDescription).
    string? Description,
    // The business branch's description (shop/hotel/clinic/school/shelter/
    // pet-shop) — what services the business offers. Null for freelancers.
    string? ServicesDescription,
    // What the provider says about the bookable SERVICE itself. PetTrainer only
    // (its offering's privateTrainingDescription, lifted here for both
    // sub-categories). Null elsewhere — a groomer's blurbs are per menu item,
    // under petGroomer...offering.session.services[].description.
    string? ServiceDescription,
    // How to contact the provider, at the top level so it doesn't have to be dug
    // out of the category branch. A business reports the e-mail + telephone it
    // registered; a freelancer registers neither, so theirs comes from their own
    // account (sign-in e-mail + verified mobile). A business that left the
    // optional telephone blank falls back the same way. Null when neither source
    // has a value.
    string? Email,
    string? MobileCountryCode,
    string? MobileNumber,
    // The provider's profile/business photo (the image they uploaded for their
    // offering); null when none set.
    string? ProfilePhotoUrl,
    // The provider-level banner (POST /providers/{id}/banner-image on the
    // provider host) — the wide picture shown on their search card. Null until
    // the provider uploads one.
    string? BannerImageUrl,
    // The provider's gallery photos (Provider.ProviderPhotos), oldest-first;
    // empty when none.
    IReadOnlyList<string> GalleryImages,
    // Aggregate over every review this provider has received — the header's
    // "4.6 (23)" plus the star histogram. AverageRating is null when nobody has
    // reviewed them yet ("No reviews yet", not 0.0).
    ReviewSummaryResponse ReviewSummary,
    // The most recent parent reviews, newest first — enough to render the profile
    // without a second call. This is a PREVIEW, not the whole set: for the full
    // list, with sorting by date or score and paging, use
    // GET /providers/{providerId}/reviews.
    IReadOnlyList<ProviderReviewItemResponse> Reviews,
    PetSitterServiceResponse? PetSitter,
    PetGroomerServiceResponse? PetGroomer,
    PetTrainerServiceResponse? PetTrainer,
    PetAdoptionSaleServiceResponse? PetAdoptionSale,
    VetServiceResponse? Vet);


public sealed record ProviderWorkingHoursDayResponse(
    int DayOfWeek,              // 0 = Sunday .. 6 = Saturday
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);

public sealed record ProviderTimeOffEntryResponse(
    Guid ClosureId,
    Guid ServiceId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

/// <summary>
/// Slim per-provider card returned by <c>GET /api/v1/providers</c> on the
/// pet-parent host. Mobile drills into <c>GET /providers/{providerId}</c>
/// for the full offering / working hours / time off shape.
/// </summary>
public sealed record ProviderSummaryResponse(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    string? DisplayName,
    string? ImageUrl,
    string City,
    string? About,
    IReadOnlyCollection<string> AnimalsHandled,
    // Does this provider take the temperament of the pet the search was filtered
    // by (?petId=)? true / false when the question can be answered, null when it
    // cannot: no pet was named, the pet has no temperament recorded (it is
    // optional), or the provider's category holds no such list — only PetSitter
    // and PetGroomer offerings record dog temperaments, so vets, trainers and
    // adoption-and-sale always read null here.
    //
    // false covers BOTH "they listed other temperaments" and "they listed none",
    // which is what keeps this in step with the hard ?dogTemperaments= filter —
    // that filter excludes a provider who recorded none, so a flag calling that
    // case unanswerable would put one provider in two different buckets on two
    // screens.
    //
    // A NON-MATCH IS STILL RETURNED. This is a hint for the app's "Other"
    // section, not a filter: a parent whose dog is aggressive would otherwise see
    // a near-empty list with no explanation, and the provider they could still
    // ring up would simply have vanished.
    bool? MatchesPetTemperament = null);

/// <summary>
/// Per-provider hit returned by the four per-service booking-search
/// endpoints (<c>GET /providers/search/day-care|night-stay|groomers|vets</c>)
/// on the pet-parent host. ServiceId lets mobile jump straight to the
/// slots / booking endpoints.
/// </summary>
/// <param name="BusinessName">Null for freelance sub-categories.</param>
/// <param name="CompletedBookings">Bookings already served (not cancelled/no-show) across all the provider's services.</param>
/// <param name="Charges">Null for a grooming search without a serviceItemCode (prices are per menu item).</param>
/// <param name="ChargesUnit"><c>PerHour | PerService | PerAppointment</c>.</param>
public sealed record ProviderSearchResultResponse(
    Guid ProviderId,
    Guid ServiceId,
    string SubCategory,
    string? BusinessName,
    int CompletedBookings,
    decimal? Charges,
    string ChargesUnit,
    string? ServiceItemCode,
    // What the provider says about the service being searched: the menu item's
    // blurb for a grooming search with a serviceItemCode, the session
    // description for trainers. Null for the other searches and for a grooming
    // search without a code (no single item is being described).
    string? Description,
    // The service image the provider uploaded for this offering (same image
    // as the discovery card's). Null when the provider hasn't set one.
    string? ImageUrl,
    // The wide banner the provider uploaded for this specific service
    // (POST /providers/{id}/services/{serviceId}/banner-image). Distinct from
    // ImageUrl. Null when the provider hasn't set a banner for this service.
    string? BannerImageUrl,
    // How many pets the provider can take at once on THIS service (the
    // offering's capacity, scoped by ServiceId — day care and night stay have
    // their own buckets; grooming capacity is shop-wide across the menu).
    // On the card because ?sortBy=PetCapacity is offered: a list the parent
    // asked to order by a number should show them the number.
    int PetCapacity = 0,
    // Does this provider take the temperament of the pet the search was filtered
    // by (?petId=)? true / false when the question can be answered, null when it
    // cannot: no pet was named, the pet has no temperament recorded (it is
    // optional), or the provider's category holds no such list — only PetSitter
    // and PetGroomer offerings record dog temperaments, so vets, trainers and
    // adoption-and-sale always read null here.
    //
    // false covers BOTH "they listed other temperaments" and "they listed none",
    // which is what keeps this in step with the hard ?dogTemperaments= filter —
    // that filter excludes a provider who recorded none, so a flag calling that
    // case unanswerable would put one provider in two different buckets on two
    // screens.
    //
    // A NON-MATCH IS STILL RETURNED. This is a hint for the app's "Other"
    // section, not a filter: a parent whose dog is aggressive would otherwise see
    // a near-empty list with no explanation, and the provider they could still
    // ring up would simply have vanished.
    bool? MatchesPetTemperament = null);

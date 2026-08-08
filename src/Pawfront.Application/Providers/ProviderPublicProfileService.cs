using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Policies;
using Pawfront.Application.ProviderBanners;
using Pawfront.Application.ProviderPhotos;
using Pawfront.Application.Reviews;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Application.Services.Vet;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Providers;

internal sealed class ProviderPublicProfileService(
    IProviderServiceLocationRegistry locationRegistry,
    IPetSitterServiceRegistry petSitter,
    IPetGroomerServiceRegistry petGroomer,
    IPetTrainerServiceRegistry petTrainer,
    IPetAdoptionSaleServiceRegistry petAdoptionSale,
    IVetServiceRegistry vet,
    IProviderAvailabilityService availabilityService,
    IProviderClosureService closureService,
    IProviderPolicyService policyService,
    IProviderPhotoService photoService,
    IProviderBannerImageService bannerImageService,
    IProviderBookingStatsReader bookingStatsReader,
    IProviderContactReader contactReader,
    IBookingReviewService reviewService) : IProviderPublicProfileService
{
    // 10-year window for "future time off" — closures rarely run beyond this,
    // and the closure list endpoint requires a bounded range. Trimmed in the
    // unlikely event a closure runs longer.
    private static readonly TimeSpan FutureTimeOffWindow = TimeSpan.FromDays(365 * 10);

    // How many reviews the profile carries inline. Enough to fill the section
    // without making a provider with hundreds of reviews an expensive read — the
    // dedicated endpoint is where the rest lives.
    private const int RecentReviewCount = 10;

    public async Task<ProviderPublicProfile> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        var location = await locationRegistry.GetByProviderIdAsync(providerId, cancellationToken);
        if (location is null)
        {
            throw new ProviderPublicProfileNotFoundException(providerId);
        }

        PetSitterServiceResult? petSitterResult = null;
        PetGroomerServiceResult? petGroomerResult = null;
        PetTrainerServiceResult? petTrainerResult = null;
        PetAdoptionSaleServiceResult? petAdoptionSaleResult = null;
        VetServiceResult? vetResult = null;
        IReadOnlyList<GroomingServiceCatalogEntry>? groomingCatalog = null;

        switch (location.ServiceCategory)
        {
            case nameof(ProviderServiceCategory.PetSitter):
                petSitterResult = await petSitter.GetAsync(providerId, cancellationToken);
                break;
            case nameof(ProviderServiceCategory.PetGroomer):
                petGroomerResult = await petGroomer.GetAsync(providerId, cancellationToken);
                groomingCatalog = petGroomer.GetServiceCatalog();
                break;
            case nameof(ProviderServiceCategory.PetTrainer):
                petTrainerResult = await petTrainer.GetAsync(providerId, cancellationToken);
                break;
            case nameof(ProviderServiceCategory.PetAdoptionAndSale):
                petAdoptionSaleResult = await petAdoptionSale.GetAsync(providerId, cancellationToken);
                break;
            case nameof(ProviderServiceCategory.Vet):
                vetResult = await vet.GetAsync(providerId, cancellationToken);
                break;
        }

        // Working hours — the availability service returns a 7-element list
        // (one per day) or an empty list if the provider hasn't saved yet.
        // Either is fine to surface as-is; the mobile UI handles both shapes.
        IReadOnlyList<DayAvailabilityResult> workingHours;
        try
        {
            var availability = await availabilityService.GetAsync(providerId, cancellationToken);
            workingHours = availability.Days;
        }
        catch (AvailabilityProviderNotFoundException)
        {
            // The provider has a service registration but no Providers row —
            // shouldn't happen in practice, but surface an empty schedule
            // rather than tipping the whole call into 404.
            workingHours = Array.Empty<DayAvailabilityResult>();
        }

        // Future time off: every active closure for the provider whose date
        // range overlaps from today onward, across all services. Past
        // closures aren't useful to a discovering parent.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var timeOff = await closureService.ListAsync(
            providerId,
            serviceId: null,
            from: today,
            to: today.AddDays((int)FutureTimeOffWindow.TotalDays),
            cancellationToken);

        // Advertised booking policy: cancellation window + accepted payment
        // (payout) methods. GetAsync returns empty/null when nothing is set,
        // so no provider-not-found handling is needed here.
        var policy = await policyService.GetAsync(providerId, cancellationToken);

        // Provider images: the profile/business photo (the offering's image) +
        // the gallery (Provider.ProviderPhotos, oldest-first).
        var profilePhotoUrl = ResolveProfilePhoto(
            petSitterResult, petGroomerResult, petTrainerResult, petAdoptionSaleResult, vetResult);
        var (description, servicesDescription) = ResolveDescriptions(
            petSitterResult, petGroomerResult, petTrainerResult, petAdoptionSaleResult, vetResult);
        var serviceDescription = ResolveServiceDescription(petTrainerResult);

        // How to reach the provider. A business registers its own e-mail +
        // telephone; a freelancer registers neither (they ARE the business), so
        // their account contact is used instead. The account is also the fallback
        // for a business that skipped the now-optional telephone.
        var businessContact = ResolveBusinessContact(
            petSitterResult, petGroomerResult, petTrainerResult, petAdoptionSaleResult, vetResult);
        // Skipped entirely for a business that registered both — only a
        // freelancer (or a blank field) needs the account read.
        var accountContact = IsBlank(businessContact.Email) || IsBlank(businessContact.TelephoneNumber)
            ? await contactReader.GetAsync(providerId, cancellationToken)
            : null;
        var email = FirstNonBlank(businessContact.Email, accountContact?.Email);
        var (mobileCountryCode, mobileNumber) =
            IsBlank(businessContact.TelephoneNumber)
                ? (Trimmed(accountContact?.MobileCountryCode), Trimmed(accountContact?.MobileNumber))
                : (Trimmed(businessContact.TelephoneCountryCode), Trimmed(businessContact.TelephoneNumber));

        var gallery = await photoService.ListAsync(providerId, cancellationToken);
        var galleryImages = gallery.Select(p => p.PhotoUrl).ToList();

        // Provider-level banner (the wide card image uploaded at registration).
        // Null when the provider hasn't set one.
        var bannerImageUrl = await bannerImageService.GetAsync(providerId, cancellationToken);

        // Served-booking count — the same "overall provider experience" figure
        // the booking-search cards show. Absent from the dictionary = zero.
        var completedCounts = await bookingStatsReader.GetCompletedBookingCountsAsync(
            new[] { providerId }, cancellationToken);
        var completedBookings = completedCounts.TryGetValue(providerId, out var count) ? count : 0;

        // Reviews: the whole-population summary for the header, plus the newest few
        // inline. One call gives both — the summary is computed over every review
        // regardless of the page, so it does not shift as a reader pages the
        // dedicated list endpoint.
        var reviews = await reviewService.ListForProviderAsync(
            new ProviderReviewQuery(
                providerId,
                ReviewSortBy.Date,
                Earnings.EarningsSortDirection.Descending,
                Skip: 0,
                Take: RecentReviewCount),
            cancellationToken);

        return new ProviderPublicProfile(
            providerId,
            location.ServiceCategory,
            location.SubCategory,
            location.Latitude,
            location.Longitude,
            workingHours,
            timeOff,
            policy.MinimumHoursBeforeCancellation,
            policy.PayoutMethods,
            completedBookings,
            description,
            servicesDescription,
            serviceDescription,
            email,
            mobileCountryCode,
            mobileNumber,
            profilePhotoUrl,
            bannerImageUrl,
            galleryImages,
            reviews.Summary,
            reviews.Items,
            petSitterResult,
            petGroomerResult,
            petTrainerResult,
            petAdoptionSaleResult,
            vetResult,
            groomingCatalog);
    }

    /// <summary>
    /// Resolves the provider's profile/business photo from whichever category
    /// offering is populated — the image the provider uploaded at registration
    /// (shop/clinic/school image, else the freelance profile image). Null when none.
    /// </summary>
    private static string? ResolveProfilePhoto(
        PetSitterServiceResult? petSitter,
        PetGroomerServiceResult? petGroomer,
        PetTrainerServiceResult? petTrainer,
        PetAdoptionSaleServiceResult? petAdoptionSale,
        VetServiceResult? vet)
    {
        if (petSitter is not null)
        {
            return petSitter.PetHotel?.ImageUrl ?? petSitter.Freelance?.ImageUrl;
        }
        if (petGroomer is not null)
        {
            return petGroomer.GroomerShop?.ImageUrl ?? petGroomer.Freelance?.ImageUrl;
        }
        if (petTrainer is not null)
        {
            return petTrainer.TrainingSchool?.ImageUrl ?? petTrainer.Freelance?.ImageUrl;
        }
        if (petAdoptionSale is not null)
        {
            return petAdoptionSale.PetShelter?.ImageUrl
                ?? petAdoptionSale.PetShop?.ImageUrl
                ?? petAdoptionSale.Freelance?.ImageUrl;
        }
        if (vet is not null)
        {
            return vet.VetClinic?.ImageUrl ?? vet.Freelance?.ImageUrl;
        }
        return null;
    }

    /// <summary>
    /// Resolves the two top-level descriptive texts from whichever category
    /// offering is populated: Description = the freelancer's "about you"
    /// (null for business sub-categories), ServicesDescription = the business
    /// branch's description (null for freelancers).
    /// </summary>
    private static (string? Description, string? ServicesDescription) ResolveDescriptions(
        PetSitterServiceResult? petSitter,
        PetGroomerServiceResult? petGroomer,
        PetTrainerServiceResult? petTrainer,
        PetAdoptionSaleServiceResult? petAdoptionSale,
        VetServiceResult? vet)
    {
        if (petSitter is not null)
        {
            return (petSitter.Freelance?.AboutYou, petSitter.PetHotel?.Description);
        }
        if (petGroomer is not null)
        {
            return (petGroomer.Freelance?.AboutYou, petGroomer.GroomerShop?.Description);
        }
        if (petTrainer is not null)
        {
            return (petTrainer.Freelance?.AboutYou, petTrainer.TrainingSchool?.Description);
        }
        if (petAdoptionSale is not null)
        {
            return (
                petAdoptionSale.Freelance?.AboutYou,
                petAdoptionSale.PetShelter?.Description ?? petAdoptionSale.PetShop?.Description);
        }
        if (vet is not null)
        {
            return (vet.Freelance?.AboutYou, vet.VetClinic?.Description);
        }
        return (null, null);
    }

    /// <summary>
    /// The description of the bookable SERVICE, lifted to the top level next to
    /// the two texts above. Only PetTrainer has one — its offering's
    /// PrivateTrainingDescription, read from whichever sub-category branch is
    /// populated. A groomer's blurbs are per menu item and stay on the items;
    /// the other categories have no per-service text.
    /// </summary>
    private static string? ResolveServiceDescription(PetTrainerServiceResult? petTrainer)
    {
        var offering = petTrainer?.TrainingSchool?.Offering ?? petTrainer?.Freelance?.Offering;
        return string.IsNullOrWhiteSpace(offering?.PrivateTrainingDescription)
            ? null
            : offering.PrivateTrainingDescription.Trim();
    }

    /// <summary>
    /// The contact captured on the BUSINESS branch of whichever category offering
    /// is populated (shop / hotel / clinic / school / shelter). Every field comes
    /// back null for a freelance sub-category — freelance registration asks for
    /// no business e-mail or telephone — which is what makes the caller fall back
    /// to the provider's account contact.
    /// </summary>
    private static (string? Email, string? TelephoneCountryCode, string? TelephoneNumber) ResolveBusinessContact(
        PetSitterServiceResult? petSitter,
        PetGroomerServiceResult? petGroomer,
        PetTrainerServiceResult? petTrainer,
        PetAdoptionSaleServiceResult? petAdoptionSale,
        VetServiceResult? vet)
    {
        if (petSitter?.PetHotel is { } hotel)
        {
            return (hotel.Email, hotel.TelephoneCountryCode, hotel.TelephoneNumber);
        }
        if (petGroomer?.GroomerShop is { } shop)
        {
            return (shop.Email, shop.TelephoneCountryCode, shop.TelephoneNumber);
        }
        if (petTrainer?.TrainingSchool is { } school)
        {
            return (school.Email, school.TelephoneCountryCode, school.TelephoneNumber);
        }
        if (petAdoptionSale?.PetShelter is { } shelter)
        {
            return (shelter.Email, shelter.TelephoneCountryCode, shelter.TelephoneNumber);
        }
        if (petAdoptionSale?.PetShop is { } petShop)
        {
            return (petShop.Email, petShop.TelephoneCountryCode, petShop.TelephoneNumber);
        }
        if (vet?.VetClinic is { } clinic)
        {
            return (clinic.Email, clinic.TelephoneCountryCode, clinic.TelephoneNumber);
        }
        return (null, null, null);
    }

    // Omitted optional registration fields read back as "" rather than null, so
    // blank has to count as "not supplied" for the fallback to kick in.
    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? Trimmed(string? value) => IsBlank(value) ? null : value!.Trim();

    private static string? FirstNonBlank(string? preferred, string? fallback) =>
        Trimmed(preferred) ?? Trimmed(fallback);
}

using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;
using Pawfront.Contracts.Providers;
using Pawfront.Contracts.Services.PetAdoptionSale;
using Pawfront.Contracts.Services.PetGroomer;
using Pawfront.Contracts.Services.PetSitter;
using Pawfront.Contracts.Services.PetTrainer;
using Pawfront.Contracts.Services.Vet;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Parent-facing provider discovery endpoint. Composes the registration row,
/// the category-specific offering, weekly availability, and future closures
/// into one round-trip payload for the mobile profile screen.
///
/// The per-category Application→Contracts mapping logic is intentionally
/// duplicated from the provider host's per-category endpoint files (PetSitter,
/// PetGroomer, etc.) — pulling them into a shared helper would touch the
/// existing convention. Kept private static + lock-step with the provider host.
/// </summary>
internal static class ProviderDetailsEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapProviderDetailsEndpoints(this IEndpointRouteBuilder builder)
    {
        // Anchor literal /providers before /providers/{providerId:guid} so
        // the list handler resolves first. (Minimal-API routing already
        // prefers the more specific match, but keeping the order explicit.)
        builder.MapGet("/providers", List);
        builder.MapGet("/providers/{providerId:guid}", Get);
        return builder;
    }

    private static async Task<IResult> List(
        string? serviceCategory,
        // Repeated query: ?animals=Dog&animals=Cat. ASP.NET binds repeated
        // scalars into a string[] for minimal APIs.
        string[]? animals,
        int? skip,
        int? take,
        IProviderDiscoveryService discoveryService,
        CancellationToken cancellationToken)
    {
        string? normalisedCategory;
        try
        {
            normalisedCategory = NormaliseServiceCategoryOrNull(serviceCategory);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceCategory", exception.Message);
        }

        var clampedTake = take is null
            ? DefaultPageSize
            : Math.Clamp(take.Value, 1, MaxPageSize);
        var clampedSkip = Math.Max(0, skip ?? 0);

        var filter = new ProviderDiscoveryFilter(
            ServiceCategory: normalisedCategory,
            Animals: animals,
            Skip: clampedSkip,
            Take: clampedTake);

        var results = await discoveryService.ListAsync(filter, cancellationToken);
        return ApiResults.Ok(results.Select(ToSummaryResponse).ToArray());
    }

    private static async Task<IResult> Get(
        Guid providerId,
        IProviderPublicProfileService publicProfileService,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await publicProfileService.GetAsync(providerId, cancellationToken);
            return ApiResults.Ok(ToResponse(profile));
        }
        catch (ProviderPublicProfileNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotRegistered", exception.Message);
        }
    }

    private static ProviderSummaryResponse ToSummaryResponse(ProviderSummary summary) =>
        new(
            summary.ProviderId,
            summary.ServiceCategory,
            summary.SubCategory,
            summary.DisplayName,
            summary.ImageUrl,
            summary.City,
            summary.About,
            summary.AnimalsHandled);

    private static string? NormaliseServiceCategoryOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim() switch
        {
            "PetSitter" => "PetSitter",
            "PetGroomer" => "PetGroomer",
            "PetTrainer" => "PetTrainer",
            "PetAdoptionAndSale" => "PetAdoptionAndSale",
            "Vet" => "Vet",
            var unsupported => throw new ArgumentException(
                $"Service category '{unsupported}' is not supported.")
        };
    }

    private static ProviderPublicProfileResponse ToResponse(ProviderPublicProfile profile)
    {
        return new ProviderPublicProfileResponse(
            profile.ProviderId,
            profile.ServiceCategory,
            profile.SubCategory,
            profile.Latitude,
            profile.Longitude,
            profile.WorkingHours
                .Select(d => new ProviderWorkingHoursDayResponse(
                    d.DayOfWeek,
                    d.IsOpen,
                    d.StartTime,
                    d.EndTime,
                    d.BreakStartTime,
                    d.BreakEndTime))
                .ToArray(),
            profile.TimeOff
                .Select(c => new ProviderTimeOffEntryResponse(
                    c.ClosureId,
                    c.ServiceId,
                    c.StartDate,
                    c.EndDate,
                    c.StartTime,
                    c.EndTime,
                    c.Reason))
                .ToArray(),
            profile.PetSitter is null ? null : ToPetSitterResponse(profile.PetSitter),
            profile.PetGroomer is null
                ? null
                : ToPetGroomerResponse(profile.PetGroomer, profile.GroomingServiceCatalog),
            profile.PetTrainer is null ? null : ToPetTrainerResponse(profile.PetTrainer),
            profile.PetAdoptionSale is null ? null : ToPetAdoptionSaleResponse(profile.PetAdoptionSale),
            profile.Vet is null ? null : ToVetResponse(profile.Vet));
    }

    // ---------------------------------------------------------------------
    // PetSitter mapping (mirror of Pawfront.Api.Endpoints.PetSitterEndpoints).
    // ---------------------------------------------------------------------
    private static PetSitterServiceResponse ToPetSitterResponse(PetSitterServiceResult result) =>
        new(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.PetHotel is null
                ? null
                : new PetHotelResponse(
                    result.PetHotel.Name,
                    result.PetHotel.TelephoneCountryCode,
                    result.PetHotel.TelephoneNumber,
                    result.PetHotel.Email,
                    result.PetHotel.Description,
                    result.PetHotel.ImageUrl,
                    ToPetSitterLicense(result.PetHotel.License),
                    ToPetSitterOffering(result.PetHotel.Offering)),
            result.Freelance is null
                ? null
                : new FreelancePetSitterResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToPetSitterLicense(result.Freelance.License),
                    ToPetSitterOffering(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);

    private static PetSitterLicenseResponse? ToPetSitterLicense(PetSitterLicenseResult? license) =>
        license is null
            ? null
            : new PetSitterLicenseResponse(license.LicenseNumber, license.LicenseType, license.ImageUrl);

    private static PetSitterOfferingResponse? ToPetSitterOffering(PetSitterOfferingResult? offering) =>
        offering is null
            ? null
            : new PetSitterOfferingResponse(
                ToBoarding(offering.DayCare),
                ToBoarding(offering.NightStay),
                offering.AnimalsHandled,
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments,
                offering.ServiceLocation,
                offering.AllowParentFood);

    private static BoardingOfferingResponse? ToBoarding(BoardingOfferingResult? offering) =>
        offering is null
            ? null
            : new BoardingOfferingResponse(
                offering.PricePerHour,
                offering.AddOns,
                offering.MinimumBookingHours,
                offering.LatePickupCharges,
                offering.DropOffTime,
                offering.PickUpTime);

    // ---------------------------------------------------------------------
    // PetGroomer mapping (mirror of Pawfront.Api.Endpoints.PetGroomerEndpoints).
    // Embeds the canonical grooming-service catalog so the mobile picker
    // can render menu items without a second fetch.
    // ---------------------------------------------------------------------
    private static PetGroomerServiceResponse ToPetGroomerResponse(
        PetGroomerServiceResult result,
        IReadOnlyList<GroomingServiceCatalogEntry>? catalog) =>
        new(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.GroomerShop is null
                ? null
                : new GroomerShopResponse(
                    result.GroomerShop.Name,
                    result.GroomerShop.TelephoneCountryCode,
                    result.GroomerShop.TelephoneNumber,
                    result.GroomerShop.Email,
                    result.GroomerShop.Description,
                    result.GroomerShop.ImageUrl,
                    ToPetGroomerLicense(result.GroomerShop.License),
                    ToPetGroomerOffering(result.GroomerShop.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceGroomerResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToPetGroomerLicense(result.Freelance.License),
                    ToPetGroomerOffering(result.Freelance.Offering)),
            catalog is null
                ? Array.Empty<GroomingServiceCatalogEntryResponse>()
                : catalog.Select(e => new GroomingServiceCatalogEntryResponse(e.Code, e.DisplayName)).ToArray(),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);

    private static PetGroomerLicenseResponse? ToPetGroomerLicense(PetGroomerLicenseResult? license) =>
        license is null
            ? null
            : new PetGroomerLicenseResponse(license.LicenseNumber, license.LicenseType, license.ImageUrl);

    private static PetGroomerOfferingResponse? ToPetGroomerOffering(PetGroomerOfferingResult? offering) =>
        offering is null
            ? null
            : new PetGroomerOfferingResponse(
                offering.Session is null
                    ? null
                    : new GroomingOfferingResponse(
                        offering.Session.Services
                            .Select(s => new GroomingServiceItemResponse(s.Code, s.Price, s.DurationMinutes, s.IsActive))
                            .ToArray(),
                        offering.Session.AddOns,
                        offering.Session.LatePickupCharges,
                        offering.Session.DropOffTime,
                        offering.Session.PickUpTime),
                offering.AnimalsHandled,
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments,
                offering.ServiceLocation);

    // ---------------------------------------------------------------------
    // PetTrainer mapping (mirror of Pawfront.Api.Endpoints.PetTrainerEndpoints).
    // ---------------------------------------------------------------------
    private static PetTrainerServiceResponse ToPetTrainerResponse(PetTrainerServiceResult result) =>
        new(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.TrainingSchool is null
                ? null
                : new TrainingSchoolResponse(
                    result.TrainingSchool.Name,
                    result.TrainingSchool.TelephoneCountryCode,
                    result.TrainingSchool.TelephoneNumber,
                    result.TrainingSchool.Email,
                    result.TrainingSchool.Description,
                    result.TrainingSchool.ImageUrl,
                    ToPetTrainerLicense(result.TrainingSchool.License),
                    ToPetTrainerOffering(result.TrainingSchool.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceTrainerResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToPetTrainerLicense(result.Freelance.License),
                    ToPetTrainerOffering(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);

    private static PetTrainerLicenseResponse? ToPetTrainerLicense(PetTrainerLicenseResult? license) =>
        license is null
            ? null
            : new PetTrainerLicenseResponse(license.LicenseNumber, license.LicenseType, license.ImageUrl);

    private static PetTrainerOfferingResponse? ToPetTrainerOffering(PetTrainerOfferingResult? offering) =>
        offering is null
            ? null
            : new PetTrainerOfferingResponse(
                offering.Session is null
                    ? null
                    : new TrainingSessionResponse(
                        offering.Session.SessionDurationHours,
                        offering.Session.PricePerSession),
                offering.PetsTrained,
                offering.AgeGroups,
                offering.Temperaments,
                offering.MaxConcurrentSessions,
                offering.ServiceLocations,
                offering.TrainingApproaches,
                offering.PreviousExperience,
                offering.PrivateTrainingDescription);

    // ---------------------------------------------------------------------
    // PetAdoptionSale mapping (basic registration only — no offering).
    // ---------------------------------------------------------------------
    private static PetAdoptionSaleServiceResponse ToPetAdoptionSaleResponse(PetAdoptionSaleServiceResult result) =>
        new(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.PetShelter is null
                ? null
                : new PetShelterResponse(
                    result.PetShelter.Name,
                    result.PetShelter.TelephoneCountryCode,
                    result.PetShelter.TelephoneNumber,
                    result.PetShelter.Email,
                    result.PetShelter.Description,
                    result.PetShelter.ImageUrl),
            result.PetShop is null
                ? null
                : new PetShopResponse(
                    result.PetShop.Name,
                    result.PetShop.TelephoneCountryCode,
                    result.PetShop.TelephoneNumber,
                    result.PetShop.Email,
                    result.PetShop.Description,
                    result.PetShop.ImageUrl),
            result.Freelance is null
                ? null
                : new FreelancePetAdoptionSaleResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);

    // ---------------------------------------------------------------------
    // Vet mapping.
    // ---------------------------------------------------------------------
    private static VetServiceResponse ToVetResponse(VetServiceResult result) =>
        new(
            result.ProviderId,
            result.SubCategory,
            result.Address,
            result.Zip,
            result.City,
            result.Website,
            result.VetClinic is null
                ? null
                : new VetClinicResponse(
                    result.VetClinic.Name,
                    result.VetClinic.TelephoneCountryCode,
                    result.VetClinic.TelephoneNumber,
                    result.VetClinic.Email,
                    result.VetClinic.Description,
                    result.VetClinic.ImageUrl,
                    ToVetCertificate(result.VetClinic.Certificate),
                    ToVetOffering(result.VetClinic.Offering)),
            result.Freelance is null
                ? null
                : new FreelanceVeterinarianResponse(
                    result.Freelance.AboutYou,
                    result.Freelance.ImageUrl,
                    ToVetCertificate(result.Freelance.Certificate),
                    ToVetOffering(result.Freelance.Offering)),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);

    private static VetCertificateResponse? ToVetCertificate(VetCertificateResult? certificate) =>
        certificate is null
            ? null
            : new VetCertificateResponse(certificate.ImageUrl);

    private static VetOfferingResponse? ToVetOffering(VetOfferingResult? offering) =>
        offering is null
            ? null
            : new VetOfferingResponse(
                offering.Appointment is null
                    ? null
                    : new VetAppointmentResponse(
                        offering.Appointment.AppointmentDurationHours,
                        offering.Appointment.PricePerAppointment),
                offering.AnimalsTreated,
                offering.MaxConcurrentConsultations,
                offering.ServiceLocation);
}

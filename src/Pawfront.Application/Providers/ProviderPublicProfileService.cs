using Pawfront.Application.Availability;
using Pawfront.Application.Closures;
using Pawfront.Application.Policies;
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
    IProviderPolicyService policyService) : IProviderPublicProfileService
{
    // 10-year window for "future time off" — closures rarely run beyond this,
    // and the closure list endpoint requires a bounded range. Trimmed in the
    // unlikely event a closure runs longer.
    private static readonly TimeSpan FutureTimeOffWindow = TimeSpan.FromDays(365 * 10);

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
            petSitterResult,
            petGroomerResult,
            petTrainerResult,
            petAdoptionSaleResult,
            vetResult,
            groomingCatalog);
    }
}

using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Onboarding;

internal sealed class ProviderOnboardingStatusService(
    IProviderOnboardingStatusReader reader,
    IPetSitterServiceRegistry petSitter,
    IPetGroomerServiceRegistry petGroomer,
    IPetTrainerServiceRegistry petTrainer,
    IPetAdoptionSaleServiceRegistry petAdoptionSale,
    IVetServiceRegistry vet) : IProviderOnboardingStatusService
{
    private static readonly string PetSitterCategory = ProviderServiceCategory.PetSitter.ToString();
    private static readonly string PetGroomerCategory = ProviderServiceCategory.PetGroomer.ToString();
    private static readonly string PetTrainerCategory = ProviderServiceCategory.PetTrainer.ToString();
    private static readonly string PetAdoptionAndSaleCategory = ProviderServiceCategory.PetAdoptionAndSale.ToString();
    private static readonly string VetCategory = ProviderServiceCategory.Vet.ToString();

    public async Task<ProviderOnboardingStatus> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.ReadAsync(providerId, cancellationToken);
        if (snapshot is null)
        {
            throw new ProviderOnboardingStatusProviderNotFoundException(providerId);
        }

        // Stage 1 — Basic Info: provider profile exists in SQL.
        var basicInfo = new OnboardingStage(OnboardingStageStatuses.Complete);

        // Stage 2 — Service Selection: at least one registered category.
        var selectedCategories = snapshot.RegisteredCategories
            .Select(c => c.Category)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var serviceSelection = new ServiceSelectionStage(
            selectedCategories.Length > 0 ? OnboardingStageStatuses.Complete : OnboardingStageStatuses.Remaining,
            selectedCategories);

        // Stage 3 — Selected Service Details: each registered category's offering filled in.
        var details = new List<SelectedServiceDetail>(snapshot.RegisteredCategories.Count);
        foreach (var registered in snapshot.RegisteredCategories)
        {
            var isComplete = await IsCategoryDetailsCompleteAsync(providerId, registered, cancellationToken);
            details.Add(new SelectedServiceDetail(registered.Category, registered.SubCategory, isComplete));
        }
        var allDetailsComplete = details.Count > 0 && details.All(d => d.IsDetailsComplete);
        var selectedServiceDetails = new SelectedServiceDetailsStage(
            allDetailsComplete ? OnboardingStageStatuses.Complete : OnboardingStageStatuses.Remaining,
            details);

        // Stage 4 — Payout & Cancellation: at least one payout method AND a cancellation policy row.
        var payoutComplete = snapshot.PayoutMethods.Count > 0;
        var cancellationComplete = snapshot.HasCancellationPolicyRow;
        var payoutAndCancellation = new PayoutAndCancellationStage(
            payoutComplete && cancellationComplete
                ? OnboardingStageStatuses.Complete
                : OnboardingStageStatuses.Remaining,
            payoutComplete,
            cancellationComplete,
            snapshot.PayoutMethods,
            snapshot.MinimumHoursBeforeCancellation);

        // Stage 5 — Verification.
        var verification = new VerificationStatus(snapshot.IsEmailVerified, snapshot.IsMobileVerified);

        var isFullyOnboarded =
            serviceSelection.Status == OnboardingStageStatuses.Complete
            && selectedServiceDetails.Status == OnboardingStageStatuses.Complete
            && payoutAndCancellation.Status == OnboardingStageStatuses.Complete
            && verification.IsEmailVerified
            && verification.IsMobileVerified;

        return new ProviderOnboardingStatus(
            providerId,
            basicInfo,
            serviceSelection,
            selectedServiceDetails,
            payoutAndCancellation,
            verification,
            isFullyOnboarded);
    }

    private async Task<bool> IsCategoryDetailsCompleteAsync(
        Guid providerId,
        RegisteredCategory registered,
        CancellationToken cancellationToken)
    {
        if (registered.Category == PetSitterCategory)
        {
            var doc = await petSitter.GetAsync(providerId, cancellationToken);
            if (doc is null) return false;
            return registered.SubCategory switch
            {
                "PetHotel" => doc.PetHotel?.Offering is not null && doc.PetHotel?.License is not null,
                "FreelancePetSitter" => doc.Freelance?.Offering is not null && doc.Freelance?.License is not null,
                _ => false
            };
        }

        if (registered.Category == PetGroomerCategory)
        {
            var doc = await petGroomer.GetAsync(providerId, cancellationToken);
            if (doc is null) return false;
            return registered.SubCategory switch
            {
                "GroomerShop" => doc.GroomerShop?.Offering is not null && doc.GroomerShop?.License is not null,
                "FreelanceGroomer" => doc.Freelance?.Offering is not null && doc.Freelance?.License is not null,
                _ => false
            };
        }

        if (registered.Category == PetTrainerCategory)
        {
            var doc = await petTrainer.GetAsync(providerId, cancellationToken);
            if (doc is null) return false;
            return registered.SubCategory switch
            {
                "TrainingSchool" => doc.TrainingSchool?.Offering is not null && doc.TrainingSchool?.License is not null,
                "FreelanceTrainer" => doc.Freelance?.Offering is not null && doc.Freelance?.License is not null,
                _ => false
            };
        }

        if (registered.Category == VetCategory)
        {
            var doc = await vet.GetAsync(providerId, cancellationToken);
            if (doc is null) return false;
            return registered.SubCategory switch
            {
                "VetClinic" => doc.VetClinic?.Offering is not null && doc.VetClinic?.Certificate is not null,
                "FreelanceVeterinarian" => doc.Freelance?.Offering is not null && doc.Freelance?.Certificate is not null,
                _ => false
            };
        }

        if (registered.Category == PetAdoptionAndSaleCategory)
        {
            // No offering stage built yet; basic registration in Cosmos is the only completion signal.
            var doc = await petAdoptionSale.GetAsync(providerId, cancellationToken);
            return doc is not null;
        }

        return false;
    }
}

namespace Pawfront.Application.Onboarding;

public sealed record ProviderOnboardingStatus(
    Guid ProviderId,
    OnboardingStage BasicInfo,
    ServiceSelectionStage ServiceSelection,
    SelectedServiceDetailsStage SelectedServiceDetails,
    PayoutAndCancellationStage PayoutAndCancellation,
    VerificationStatus Verification,
    bool IsFullyOnboarded);

public sealed record OnboardingStage(string Status);

public sealed record ServiceSelectionStage(
    string Status,
    IReadOnlyCollection<string> SelectedCategories);

public sealed record SelectedServiceDetailsStage(
    string Status,
    IReadOnlyCollection<SelectedServiceDetail> Categories);

public sealed record SelectedServiceDetail(
    string Category,
    string SubCategory,
    bool IsDetailsComplete);

public sealed record PayoutAndCancellationStage(
    string Status,
    bool IsPayoutComplete,
    bool IsCancellationPolicyComplete,
    IReadOnlyCollection<string> PayoutMethods,
    int? MinimumHoursBeforeCancellation);

public sealed record VerificationStatus(
    bool IsEmailVerified,
    bool IsMobileVerified);

public static class OnboardingStageStatuses
{
    public const string Complete = "Complete";
    public const string Remaining = "Remaining";
}

public sealed record ProviderOnboardingStatusSnapshot(
    Guid ProviderId,
    bool IsMobileVerified,
    bool IsEmailVerified,
    IReadOnlyCollection<RegisteredCategory> RegisteredCategories,
    IReadOnlyCollection<string> PayoutMethods,
    bool HasCancellationPolicyRow,
    int? MinimumHoursBeforeCancellation);

public sealed record RegisteredCategory(string Category, string SubCategory);

public sealed class ProviderOnboardingStatusProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

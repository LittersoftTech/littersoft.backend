namespace Pawfront.Contracts.Onboarding;

public sealed record ProviderOnboardingStatusResponse(
    Guid ProviderId,
    OnboardingStageResponse BasicInfo,
    ServiceSelectionStageResponse ServiceSelection,
    SelectedServiceDetailsStageResponse SelectedServiceDetails,
    PayoutAndCancellationStageResponse PayoutAndCancellation,
    VerificationStatusResponse Verification,
    bool IsFullyOnboarded);

public sealed record OnboardingStageResponse(string Status);

public sealed record ServiceSelectionStageResponse(
    string Status,
    IReadOnlyCollection<string> SelectedCategories);

public sealed record SelectedServiceDetailsStageResponse(
    string Status,
    IReadOnlyCollection<SelectedServiceDetailResponse> Categories);

public sealed record SelectedServiceDetailResponse(
    string Category,
    string SubCategory,
    bool IsDetailsComplete);

public sealed record PayoutAndCancellationStageResponse(
    string Status,
    bool IsPayoutComplete,
    bool IsCancellationPolicyComplete,
    IReadOnlyCollection<string> PayoutMethods,
    int? MinimumHoursBeforeCancellation);

public sealed record VerificationStatusResponse(
    bool IsEmailVerified,
    bool IsMobileVerified);

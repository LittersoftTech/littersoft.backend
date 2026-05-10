using Pawfront.Api.Auth;
using Pawfront.Application.Onboarding;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Onboarding;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Api.Endpoints;

internal static class ProviderOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapProviderOnboardingEndpoints(this IEndpointRouteBuilder builder)
    {
        var onboarding = builder.MapGroup("/provider-onboarding");
        onboarding.MapPost("/firebase-auth", SaveFirebaseAuth);
        onboarding.MapPost("/profile", CompleteProfile);

        var mobileVerification = builder.MapGroup("/providers/{providerId:guid}/mobile-verification");
        mobileVerification.MapPost("/otp", SendOtp);
        mobileVerification.MapPost("/otp/{providerMobileOtpId:guid}/verify", VerifyOtp);

        builder.MapGet("/providers/{providerId:guid}/onboarding-status", GetOnboardingStatus);

        return builder;
    }

    private static async Task<IResult> SaveFirebaseAuth(
        SaveProviderFirebaseAuthRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = FirebaseClaims.BuildCommand(httpContext.User, request);
            var response = await onboardingService.SaveFirebaseAuthAsync(command, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (UnsupportedAuthProviderException exception)
        {
            return ApiResults.BadRequest("UnsupportedAuthProvider", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> CompleteProfile(
        CompleteProviderProfileRequest request,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.CompleteProviderProfileAsync(request, cancellationToken);
            return ApiResults.Created($"/api/v1/providers/{response.ProviderId}", response);
        }
        catch (ProviderAuthIdentityNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderAuthIdentityNotFound", exception.Message);
        }
        catch (MobileNumberAlreadyExistsException exception)
        {
            return ApiResults.Conflict("MobileNumberAlreadyExists", exception.Message);
        }
        catch (UnsupportedGenderException exception)
        {
            return ApiResults.BadRequest("UnsupportedGender", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SendOtp(
        Guid providerId,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.SendProviderMobileOtpAsync(providerId, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (ProviderProfileNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
    }

    private static async Task<IResult> VerifyOtp(
        Guid providerId,
        Guid providerMobileOtpId,
        VerifyProviderMobileOtpRequest request,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.VerifyProviderMobileOtpAsync(
                providerId,
                providerMobileOtpId,
                request,
                cancellationToken);

            return ApiResults.Ok(response);
        }
        catch (ProviderMobileOtpNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderMobileOtpNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetOnboardingStatus(
        Guid providerId,
        IProviderOnboardingStatusService statusService,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await statusService.GetAsync(providerId, cancellationToken);
            return ApiResults.Ok(ToResponse(status));
        }
        catch (ProviderOnboardingStatusProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
    }

    private static ProviderOnboardingStatusResponse ToResponse(ProviderOnboardingStatus status)
    {
        return new ProviderOnboardingStatusResponse(
            status.ProviderId,
            new OnboardingStageResponse(status.BasicInfo.Status),
            new ServiceSelectionStageResponse(
                status.ServiceSelection.Status,
                status.ServiceSelection.SelectedCategories),
            new SelectedServiceDetailsStageResponse(
                status.SelectedServiceDetails.Status,
                status.SelectedServiceDetails.Categories
                    .Select(c => new SelectedServiceDetailResponse(c.Category, c.SubCategory, c.IsDetailsComplete))
                    .ToArray()),
            new PayoutAndCancellationStageResponse(
                status.PayoutAndCancellation.Status,
                status.PayoutAndCancellation.IsPayoutComplete,
                status.PayoutAndCancellation.IsCancellationPolicyComplete,
                status.PayoutAndCancellation.PayoutMethods,
                status.PayoutAndCancellation.MinimumHoursBeforeCancellation),
            new VerificationStatusResponse(
                status.Verification.IsEmailVerified,
                status.Verification.IsMobileVerified),
            status.IsFullyOnboarded);
    }
}

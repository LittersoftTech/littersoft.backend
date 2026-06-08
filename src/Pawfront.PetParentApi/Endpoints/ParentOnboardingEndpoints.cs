using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

internal static class ParentOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapParentOnboardingEndpoints(this IEndpointRouteBuilder builder)
    {
        var onboarding = builder.MapGroup("/parent-onboarding");
        onboarding.MapPost("/firebase-auth", SaveFirebaseAuth);
        onboarding.MapPost("/profile", CompleteProfile);
        onboarding.MapGet("/me", ResolvePetParentFromFirebaseToken);

        return builder;
    }

    private static async Task<IResult> ResolvePetParentFromFirebaseToken(
        HttpContext httpContext,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var response = await onboardingService.ResolvePetParentByFirebaseUidAsync(
                firebaseUserId,
                cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (ParentAuthIdentityNotFoundException exception)
        {
            return ApiResults.NotFound("ParentAuthIdentityNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> CompleteProfile(
        HttpContext httpContext,
        CompletePetParentProfileRequest request,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            // The auth identity is resolved from the JWT here — the request
            // body intentionally has no ParentAuthIdentityId field, so a
            // caller cannot complete another user's profile.
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var response = await onboardingService.CompletePetParentProfileAsync(
                firebaseUserId, request, cancellationToken);
            return ApiResults.Created($"/api/v1/pet-parents/{response.PetParentId}", response);
        }
        catch (ParentAuthIdentityNotFoundException exception)
        {
            return ApiResults.NotFound("ParentAuthIdentityNotFound", exception.Message);
        }
        catch (PetParentMobileNumberAlreadyExistsException exception)
        {
            return ApiResults.Conflict("MobileNumberAlreadyExists", exception.Message);
        }
        catch (UnsupportedPetParentGenderException exception)
        {
            return ApiResults.BadRequest("UnsupportedGender", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SaveFirebaseAuth(
        SaveParentFirebaseAuthRequest request,
        HttpContext httpContext,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = FirebaseClaims.BuildCommand(httpContext.User, request);
            var response = await onboardingService.SaveFirebaseAuthAsync(command, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (UnsupportedParentAuthProviderException exception)
        {
            return ApiResults.BadRequest("UnsupportedAuthProvider", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }
}

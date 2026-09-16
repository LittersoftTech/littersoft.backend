using Pawfront.Application.DeviceTokens;
using Pawfront.Contracts.DeviceTokens;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// FCM device-token registration for the pet-parent app.
///
/// Deliberately NOT under the ownership-filtered <c>/pet-parents/{id}</c> group:
/// the token tables key on the AUTH IDENTITY, and a parent who has signed in but
/// not yet completed their profile has no PetParentId — that group would reject
/// them with 403 ParentProfileNotCompleted. Registering early is correct; the
/// PetParentId is back-filled when the profile is created. Same reasoning as
/// <c>/parent-onboarding/firebase-auth</c> not being filtered.
/// </summary>
internal static class DeviceTokenEndpoints
{
    public static IEndpointRouteBuilder MapDeviceTokenEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/device-tokens");
        group.MapPost("/", RegisterDeviceToken);
        // POST, not DELETE. Minimal APIs refuse an inferred body on DELETE, and
        // the two alternatives are worse: putting the FCM token in the URL leaks
        // it into every access log along the way, and a DELETE body is stripped
        // by some proxies — a sign-out that silently fails would leave the device
        // receiving the previous account's notifications. "Deactivate" is also
        // the honest verb: the row is kept with IsActive = 0, never deleted.
        group.MapPost("/deactivate", DeactivateDeviceToken);

        return builder;
    }

    private static async Task<IResult> RegisterDeviceToken(
        RegisterDeviceTokenRequest request,
        HttpContext httpContext,
        IPetParentDeviceTokenService deviceTokenService,
        CancellationToken cancellationToken)
    {
        try
        {
            // Owner comes from the JWT, never the body — a caller can only ever
            // bind a token to themselves.
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var response = await deviceTokenService.RegisterAsync(firebaseUserId, request, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (DeviceTokenIdentityNotFoundException exception)
        {
            return ApiResults.NotFound("ParentAuthIdentityNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> DeactivateDeviceToken(
        DeactivateDeviceTokenRequest request,
        HttpContext httpContext,
        IPetParentDeviceTokenService deviceTokenService,
        CancellationToken cancellationToken)
    {
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var response = await deviceTokenService.DeactivateAsync(
                firebaseUserId, request.FcmToken, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (DeviceTokenNotFoundException exception)
        {
            return ApiResults.NotFound("DeviceTokenNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }
}

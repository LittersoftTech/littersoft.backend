using Pawfront.Api.Auth;
using Pawfront.Application.DeviceTokens;
using Pawfront.Contracts.DeviceTokens;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// FCM device-token registration for the provider app. Mirror of the pet-parent
/// host's endpoints.
///
/// Not scoped under <c>/providers/{providerId}</c>: the token table keys on the
/// AUTH IDENTITY, and a provider who has signed in but not completed their
/// profile has no ProviderId yet. Registering early is correct; the ProviderId is
/// back-filled when the profile is created.
/// </summary>
internal static class DeviceTokenEndpoints
{
    public static IEndpointRouteBuilder MapDeviceTokenEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/device-tokens");
        group.MapPost("/", RegisterDeviceToken);
        // POST, not DELETE — see the pet-parent host's copy for the reasoning:
        // minimal APIs refuse an inferred body on DELETE, the token must not go
        // in the URL, and a stripped DELETE body would make sign-out fail
        // silently. The row is deactivated, not deleted, so the verb fits.
        group.MapPost("/deactivate", DeactivateDeviceToken);

        return builder;
    }

    private static async Task<IResult> RegisterDeviceToken(
        RegisterDeviceTokenRequest request,
        HttpContext httpContext,
        IProviderDeviceTokenService deviceTokenService,
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
            return ApiResults.NotFound("ProviderAuthIdentityNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> DeactivateDeviceToken(
        DeactivateDeviceTokenRequest request,
        HttpContext httpContext,
        IProviderDeviceTokenService deviceTokenService,
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

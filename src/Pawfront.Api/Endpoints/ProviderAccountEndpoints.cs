using Pawfront.Api.Auth;
using Pawfront.Application.ProviderOnboarding;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Provider account lifecycle. Today: the hard delete behind the app's
/// "Delete account" action.
/// </summary>
internal static class ProviderAccountEndpoints
{
    public static IEndpointRouteBuilder MapProviderAccountEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapDelete("/providers/{providerId:guid}", DeleteProviderAccount);
        return builder;
    }

    private static async Task<IResult> DeleteProviderAccount(
        Guid providerId,
        HttpContext httpContext,
        IProviderAccountService accountService,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        // The delete is irreversible, so — unlike the rest of this host, which
        // trusts the route id — the caller must be the provider being deleted.
        // The id comes from the JWT, never the route or body.
        Guid? callerProviderId;
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId,
                cancellationToken);
            callerProviderId = caller.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            callerProviderId = null;
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (callerProviderId != providerId)
        {
            return ApiResults.Forbidden(
                "Forbidden",
                "You can only delete your own provider account.");
        }

        try
        {
            var response = await accountService.DeleteAsync(providerId, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (ProviderProfileNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
    }
}

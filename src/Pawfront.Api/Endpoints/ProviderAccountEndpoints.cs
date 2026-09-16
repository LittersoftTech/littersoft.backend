using Pawfront.Api.Auth;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Support;

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
        // An open support ticket blocks the delete — part of the legal hold, since
        // anonymising one party while support is still looking at the dispute would
        // erase what the ticket is about. Nothing was changed; the account is
        // untouched. The provider cannot clear this themselves — only support closing
        // the ticket lifts it — and there is no force override.
        catch (ProviderOpenTicketsException exception)
        {
            return ApiResults.Conflict(
                "OpenTicketsExist",
                exception.Message,
                new OpenTicketsForProviderResponse(
                    exception.ProviderId,
                    [.. exception.OpenTickets.Select(SupportTicketMapping.ToResponse)]));
        }
    }
}

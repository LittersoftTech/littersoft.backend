using Pawfront.Application.Analytics;
using Pawfront.Contracts.Analytics;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Records a pet parent looking at a provider — the capture behind the provider's
/// PawPrints "Views" card.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS WHY THE ANALYTICS HAVE ANYTHING TO SHOW. Nothing in the product
/// recorded a provider or service view before: <c>Event.Events</c> keeps three
/// bare counters for events, and that is all there was. Without this route the
/// provider's views figure, its per-service breakdown and its viewer list are
/// zero forever.
/// </para>
/// <para>
/// PARENT HOST ONLY, on purpose: a provider opening their own profile is not a
/// view, and the provider host has no parent identity to attribute one to.
/// </para>
/// <para>
/// NOT ownership-filtered, and it must not be — <c>/providers/*</c> is browsable
/// before onboarding completes, which is exactly when a lot of looking happens.
/// The caller's PetParentId is resolved from the JWT (never from the body) and is
/// simply recorded as null when they have no profile yet; the view still counts
/// toward the provider's total but cannot appear in their viewer list.
/// </para>
/// <para>
/// Deliberately NOT wired into <c>GET /providers/{providerId}</c> as a side
/// effect. Recording a view is a write, a GET is not the place for one, and the
/// app knows things the server cannot infer — which service card was tapped, which
/// pet the parent is shopping for, and which surface they came from. That is also
/// why the counter-style <c>POST /events/{eventId}/views</c> precedent is followed
/// rather than the alternative.
/// </para>
/// </remarks>
internal static class ProviderViewEndpoints
{
    public static IEndpointRouteBuilder MapProviderViewEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/providers/{providerId:guid}/views", RecordView);
        return builder;
    }

    private static async Task<IResult> RecordView(
        Guid providerId,
        RecordProviderViewRequest? request,
        ICurrentPetParentContext currentPetParent,
        IProviderAnalyticsService analyticsService,
        CancellationToken cancellationToken)
    {
        var petParentId = await currentPetParent.GetPetParentIdAsync(cancellationToken);

        // A petId is only meaningful alongside a parent: the procedure checks the
        // pet belongs to THIS parent, so sending one without a resolved profile
        // could only ever be refused. Dropping it here keeps a pre-profile browse
        // recordable instead of turning it into a 400 the caller cannot fix.
        var petId = petParentId is null ? null : request?.PetId;

        try
        {
            var record = await analyticsService.RecordViewAsync(
                new RecordProviderViewCommand(
                    providerId,
                    request?.ServiceId,
                    petParentId,
                    petId,
                    request?.Source),
                cancellationToken);

            return ApiResults.Ok(new ProviderViewRecordedResponse(
                record.ProviderServiceViewId,
                record.ProviderId,
                record.ServiceId,
                record.PetParentId,
                record.PetId,
                record.Source,
                record.ViewedAtUtc));
        }
        catch (ProviderViewProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
        catch (ProviderViewInvalidServiceException exception)
        {
            // Refused rather than dropped: a service belonging to somebody else
            // would put a stranger's views in this provider's breakdown.
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (ProviderViewInvalidPetException exception)
        {
            // Refused rather than dropped: another parent's pet would put the wrong
            // breed on a card the provider is about to act on.
            return ApiResults.BadRequest("InvalidPetId", exception.Message);
        }
    }
}

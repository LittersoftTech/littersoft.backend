using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Api.Endpoints;

internal static class ProviderActiveStatusEndpoints
{
    public static IEndpointRouteBuilder MapProviderActiveStatusEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/providers/{providerId:guid}/active-status", SetActiveStatus);
        return builder;
    }

    private static async Task<IResult> SetActiveStatus(
        Guid providerId,
        SetProviderActiveStatusRequest request,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var outcome = await onboardingService.SetActiveStatusAsync(
                providerId,
                request.IsActive,
                cancellationToken);

            return outcome switch
            {
                SetActiveStatusOutcome.Updated updated => ApiResults.Ok(new SetProviderActiveStatusResponse(
                    Status: SetProviderActiveStatusResult.Updated,
                    ProviderId: updated.ProviderId,
                    IsActive: updated.IsActive,
                    UpdatedAtUtc: updated.UpdatedAtUtc,
                    ConflictingBookings: null,
                    WarningMessage: null)),

                SetActiveStatusOutcome.BookingsExist conflict => ApiResults.Ok(new SetProviderActiveStatusResponse(
                    Status: SetProviderActiveStatusResult.BookingsExist,
                    ProviderId: providerId,
                    IsActive: null,
                    UpdatedAtUtc: null,
                    ConflictingBookings: conflict.Bookings
                        .Select(b => new ActiveStatusConflictingBookingSummary(
                            b.BookingId, b.ServiceId, b.ServiceCategory, b.SubCategory,
                            b.PetParentId, b.Source, b.CustomerName,
                            b.BookingDate, b.StartTime, b.EndTime))
                        .ToArray(),
                    WarningMessage:
                        $"{conflict.Bookings.Count} future confirmed booking(s) exist across this provider's services. " +
                        "Please move or cancel these bookings before deactivating.")),

                _ => throw new InvalidOperationException("Unknown SetActiveStatusOutcome variant.")
            };
        }
        catch (ProviderProfileNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
    }
}

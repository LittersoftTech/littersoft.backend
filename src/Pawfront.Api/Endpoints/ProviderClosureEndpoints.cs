using Pawfront.Application.Closures;
using Pawfront.Contracts.Closures;

namespace Pawfront.Api.Endpoints;

internal static class ProviderClosureEndpoints
{
    public static IEndpointRouteBuilder MapProviderClosureEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/closures");

        group.MapPost("/", CreateClosure);
        group.MapGet("/", ListClosures);
        group.MapDelete("/{closureId:guid}", DeleteClosure);

        return builder;
    }

    private static async Task<IResult> CreateClosure(
        Guid providerId,
        CreateProviderClosureRequest request,
        IProviderClosureService closureService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        if (request.ServiceIds is null || request.ServiceIds.Count == 0)
        {
            return ApiResults.BadRequest(
                "InvalidRequest",
                "At least one serviceId is required.");
        }

        try
        {
            var result = await closureService.CreateAsync(
                new CreateProviderClosureCommand(
                    providerId,
                    request.ServiceIds,
                    request.StartDate,
                    request.EndDate,
                    request.StartTime,
                    request.EndTime,
                    request.Reason),
                cancellationToken);

            return result switch
            {
                CreateClosureResult.Created created => ApiResults.Ok(new CreateProviderClosureResponse(
                    Status: ProviderClosureCreationStatus.Created,
                    Closures: created.Closures.Select(ToSummary).ToArray(),
                    ConflictingBookings: null,
                    WarningMessage: null)),

                CreateClosureResult.BookingsExist conflict => ApiResults.Ok(new CreateProviderClosureResponse(
                    Status: ProviderClosureCreationStatus.BookingsExist,
                    Closures: null,
                    ConflictingBookings: conflict.Bookings
                        .Select(b => new ConflictingBookingSummary(
                            b.ServiceId, b.BookingId, b.PetParentId, b.BookingDate, b.StartTime, b.EndTime))
                        .ToArray(),
                    WarningMessage:
                        $"{conflict.Bookings.Count} existing booking(s) inside the requested closure window across the targeted service(s). " +
                        "Please move or cancel these bookings before closing the service(s).")),

                _ => throw new InvalidOperationException("Unknown CreateClosureResult variant.")
            };
        }
        catch (ProviderClosureProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ProviderClosureServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (ProviderClosureEmptyServiceIdsException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListClosures(
        Guid providerId,
        Guid? serviceId,
        DateOnly? from,
        DateOnly? to,
        IProviderClosureService closureService,
        CancellationToken cancellationToken)
    {
        try
        {
            var closures = await closureService.ListAsync(providerId, serviceId, from, to, cancellationToken);
            return ApiResults.Ok(new ProviderClosuresResponse(
                providerId,
                closures.Select(ToSummary).ToArray()));
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> DeleteClosure(
        Guid providerId,
        Guid closureId,
        IProviderClosureService closureService,
        CancellationToken cancellationToken)
    {
        try
        {
            await closureService.DeleteAsync(providerId, closureId, cancellationToken);
            return ApiResults.Ok<object?>(null);
        }
        catch (ProviderClosureNotFoundException exception)
        {
            return ApiResults.NotFound("ClosureNotFound", exception.Message);
        }
    }

    private static ProviderClosureSummary ToSummary(ProviderClosure c) => new(
        c.ClosureId, c.ProviderId, c.ServiceId, c.StartDate, c.EndDate,
        c.StartTime, c.EndTime, c.Reason, c.CreatedAtUtc);
}

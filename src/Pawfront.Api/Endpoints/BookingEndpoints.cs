using Pawfront.Application.Bookings;
using Pawfront.Contracts.Bookings;

namespace Pawfront.Api.Endpoints;

internal static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var providerScoped = builder.MapGroup("/providers/{providerId:guid}/bookings");
        providerScoped.MapPost("/", CreateBooking);
        providerScoped.MapGet("/", ListByProvider);

        builder.MapGet("/bookings/{bookingId:guid}", GetBooking);
        builder.MapPost("/bookings/{bookingId:guid}/cancel", CancelBooking);

        builder.MapGet("/pet-parents/{petParentId:guid}/bookings", ListByPetParent);

        return builder;
    }

    private static async Task<IResult> CreateBooking(
        Guid providerId,
        CreateBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CreateAsync(
                new CreateBookingCommand(
                    providerId,
                    request.PetParentId,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime),
                cancellationToken);

            return ApiResults.Created($"/api/v1/bookings/{result.BookingId}", ToResponse(result));
        }
        catch (BookingProviderNotRegisteredException exception)
        {
            return ApiResults.NotFound("ServiceNotRegistered", exception.Message);
        }
        catch (BookingOfferingNotConfiguredException exception)
        {
            return ApiResults.BadRequest("OfferingNotConfigured", exception.Message);
        }
        catch (BookingProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
        catch (BookingPetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (BookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetBooking(
        Guid bookingId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var result = await bookingService.GetAsync(bookingId, cancellationToken);
        return result is null
            ? ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.")
            : ApiResults.Ok(ToResponse(result));
    }

    private static async Task<IResult> CancelBooking(
        Guid bookingId,
        CancelBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CancelAsync(bookingId, request.PetParentId, cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
        catch (BookingCancellationForbiddenException exception)
        {
            return ApiResults.BadRequest("BookingCancellationForbidden", exception.Message);
        }
        catch (BookingAlreadyCancelledException exception)
        {
            return ApiResults.Conflict("BookingAlreadyCancelled", exception.Message);
        }
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByProviderAsync(providerId, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> ListByPetParent(
        Guid petParentId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static BookingResponse ToResponse(BookingResult result) =>
        new(result.BookingId,
            result.ProviderId,
            result.PetParentId,
            result.ServiceCategory,
            result.SubCategory,
            result.BookingDate,
            result.StartTime,
            result.EndTime,
            result.Status,
            result.CreatedAtUtc,
            result.UpdatedAtUtc,
            result.CancelledAtUtc);
}

using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Contracts.Bookings;

namespace Pawfront.Api.Endpoints;

internal static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var providerScoped = builder.MapGroup("/providers/{providerId:guid}/bookings");
        providerScoped.MapPost("/", CreateBooking);
        providerScoped.MapPost("/custom", CreateCustomBooking);
        providerScoped.MapGet("/", ListByProvider);
        providerScoped.MapPost("/{bookingId:guid}/status", UpdateStatus);
        providerScoped.MapGet("/{bookingId:guid}/status-history", GetStatusHistory);

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
                    request.ServiceId,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime,
                    request.ServiceItemCode),
                cancellationToken);

            return ApiResults.Created($"/api/v1/bookings/{result.BookingId}", ToResponse(result));
        }
        catch (BookingServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
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
        catch (BookingProviderInactiveException exception)
        {
            return ApiResults.Conflict("ProviderInactive", exception.Message);
        }
        catch (BookingGroomingItemCodeRequiredException exception)
        {
            return ApiResults.BadRequest("ServiceItemCodeRequired", exception.Message);
        }
        catch (BookingGroomingItemNotOfferedException exception)
        {
            return ApiResults.BadRequest("ServiceItemNotOffered", exception.Message);
        }
        catch (BookingGroomingItemInactiveException exception)
        {
            return ApiResults.Conflict("ServiceItemInactive", exception.Message);
        }
        catch (BookingPetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (BookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (BookingNightStayUseDedicatedEndpointException exception)
        {
            return ApiResults.BadRequest("UseNightStayEndpoint", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> CreateCustomBooking(
        Guid providerId,
        CreateCustomBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var result = await bookingService.CreateCustomAsync(
                new CreateCustomBookingCommand(
                    providerId,
                    request.ServiceId,
                    request.CustomerName,
                    request.CustomerMobileCountryCode,
                    request.CustomerMobile,
                    request.AnimalType,
                    request.PetName,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime,
                    request.ServiceLocation,
                    request.CustomerLocation,
                    request.PricePerHour,
                    request.JobNotes),
                cancellationToken);

            return ApiResults.Created($"/api/v1/bookings/{result.BookingId}", ToResponse(result));
        }
        catch (BookingServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (BookingOfferingNotConfiguredException exception)
        {
            return ApiResults.BadRequest("OfferingNotConfigured", exception.Message);
        }
        catch (BookingProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
        catch (BookingProviderInactiveException exception)
        {
            return ApiResults.Conflict("ProviderInactive", exception.Message);
        }
        catch (BookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (BookingNightStayUseDedicatedEndpointException exception)
        {
            return ApiResults.BadRequest("UseNightStayEndpoint", exception.Message);
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

    private static async Task<IResult> UpdateStatus(
        Guid providerId,
        Guid bookingId,
        UpdateBookingStatusRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Status))
        {
            return ApiResults.BadRequest("InvalidRequest", "A status is required.");
        }

        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateBookingStatusCommand(
                    bookingId,
                    request.Status,
                    BookingStatusActor.Provider,
                    providerId,
                    request.Note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (UnsupportedBookingStatusException exception)
        {
            return ApiResults.BadRequest("UnsupportedBookingStatus", exception.Message);
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
        catch (BookingStatusForbiddenException exception)
        {
            return ApiResults.Forbidden("Forbidden", exception.Message);
        }
        catch (BookingStatusNotAllowedException exception)
        {
            return ApiResults.BadRequest("BookingStatusNotAllowed", exception.Message);
        }
        catch (BookingStatusTerminalException exception)
        {
            return ApiResults.Conflict("BookingStatusTerminal", exception.Message);
        }
        catch (BookingStatusUnchangedException exception)
        {
            return ApiResults.Conflict("BookingStatusUnchanged", exception.Message);
        }
    }

    private static async Task<IResult> GetStatusHistory(
        Guid providerId,
        Guid bookingId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // Don't leak other providers' bookings: confirm the booking belongs to
        // this provider before returning its audit trail.
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        var history = await bookingService.ListStatusHistoryAsync(bookingId, cancellationToken);
        return ApiResults.Ok(history.Select(ToHistoryResponse).ToArray());
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId,
        DateOnly? date,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByProviderAsync(providerId, date, cancellationToken);
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

    private static BookingStatusHistoryEntryResponse ToHistoryResponse(BookingStatusHistoryEntry entry) =>
        new(entry.BookingStatusHistoryId,
            entry.BookingId,
            entry.FromStatus,
            entry.ToStatus,
            entry.ChangedByActor,
            entry.ChangedByActorId,
            entry.Note,
            entry.ChangedAtUtc);

    private static BookingResponse ToResponse(BookingResult result) =>
        new(result.BookingId,
            result.ProviderId,
            result.PetParentId,
            result.ServiceId,
            result.ServiceCategory,
            result.SubCategory,
            result.BookingDate,
            result.StartTime,
            result.EndTime,
            result.Status,
            result.CreatedAtUtc,
            result.UpdatedAtUtc,
            result.CancelledAtUtc,
            result.ServiceItemCode,
            result.Source,
            result.CustomerName,
            result.CustomerMobileCountryCode,
            result.CustomerMobile,
            result.AnimalType,
            result.PetName,
            result.ServiceLocation,
            result.CustomerLocation,
            result.PricePerHour,
            result.JobNotes,
            result.PetId);
}

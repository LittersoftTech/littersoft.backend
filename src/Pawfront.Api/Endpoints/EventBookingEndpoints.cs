using Pawfront.Application.Events;
using Pawfront.Contracts.Events;

namespace Pawfront.Api.Endpoints;

internal static class EventBookingEndpoints
{
    public static IEndpointRouteBuilder MapEventBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var eventScoped = builder.MapGroup("/events/{eventId:guid}/bookings");
        eventScoped.MapPost("/", Create);

        builder.MapGet("/event-bookings/{bookingId:guid}", GetById);
        builder.MapPost("/event-bookings/{bookingId:guid}/payment-confirmation", ConfirmPayment);

        return builder;
    }

    private static async Task<IResult> Create(
        Guid eventId,
        CreateEventBookingRequest request,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CreateAsync(
                new CreateEventBookingCommand(
                    eventId,
                    request.BookerName,
                    request.BookerEmail,
                    request.BookerMobile,
                    request.AttendeeNames,
                    request.PaymentMethod),
                cancellationToken);

            return ApiResults.Created($"/api/v1/event-bookings/{result.BookingId}", ToResponse(result));
        }
        catch (EventBookingEventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (EventBookingNotPhysicalException exception)
        {
            return ApiResults.BadRequest("EventNotBookable", exception.Message);
        }
        catch (EventBookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("EventSoldOut", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetById(
        Guid bookingId,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var result = await bookingService.GetAsync(bookingId, cancellationToken);
        return result is null
            ? ApiResults.NotFound("EventBookingNotFound", $"Event booking '{bookingId}' was not found.")
            : ApiResults.Ok(ToResponse(result));
    }

    private static async Task<IResult> ConfirmPayment(
        Guid bookingId,
        ConfirmEventBookingPaymentRequest request,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.ConfirmPaymentAsync(
                bookingId,
                request.PaymentStatus,
                request.PaymentReference,
                cancellationToken);

            // ConfirmPayment returns the booking without re-reading tickets — hydrate
            // for the response so the client sees the full shape it got on create.
            var hydrated = await bookingService.GetAsync(bookingId, cancellationToken);
            return ApiResults.Ok(ToResponse(hydrated ?? result));
        }
        catch (EventBookingNotFoundException exception)
        {
            return ApiResults.NotFound("EventBookingNotFound", exception.Message);
        }
        catch (EventBookingPaymentAlreadyConfirmedException exception)
        {
            return ApiResults.Conflict("PaymentAlreadyConfirmed", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static EventBookingResponse ToResponse(EventBookingResult result)
    {
        var tickets = new List<EventBookingTicketResponse>(result.Tickets.Count);
        foreach (var ticket in result.Tickets)
        {
            tickets.Add(new EventBookingTicketResponse(ticket.TicketId, ticket.TicketNumber, ticket.AttendeeName));
        }

        return new EventBookingResponse(
            result.BookingId,
            result.EventId,
            result.BookerName,
            result.BookerEmail,
            result.BookerMobile,
            result.TicketCount,
            result.PaymentMethod,
            result.PaymentStatus,
            result.PaymentReference,
            result.TotalAmount,
            result.Status,
            tickets,
            result.CreatedAtUtc,
            result.UpdatedAtUtc,
            result.CancelledAtUtc);
    }
}

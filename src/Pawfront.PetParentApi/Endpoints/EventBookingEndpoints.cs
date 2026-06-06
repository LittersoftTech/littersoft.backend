using Pawfront.Application.Events;
using Pawfront.Contracts.Events;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Event ticket booking endpoints on the pet-parent host. Mirrors the
/// provider host's booking POST + booking GET (same shared
/// <see cref="IEventBookingService"/> backing) — duplicated because the two
/// hosts authenticate against different Firebase projects.
///
/// The payment-confirmation endpoint is intentionally NOT mirrored here:
/// it's a gateway webhook, not a user-initiated call, and is already
/// reachable on the provider host. If we ever want the pet-parent host to
/// double as a webhook receiver, that one line can come over.
/// </summary>
internal static class EventBookingEndpoints
{
    public static IEndpointRouteBuilder MapEventBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var eventScoped = builder.MapGroup("/events/{eventId:guid}/bookings");
        eventScoped.MapPost("/", Create);

        builder.MapGet("/event-bookings/{bookingId:guid}", GetById);

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

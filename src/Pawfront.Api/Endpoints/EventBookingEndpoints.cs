using System.Security.Claims;
using Pawfront.Api.Auth;
using Pawfront.Application.Events;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Events;

namespace Pawfront.Api.Endpoints;

internal static class EventBookingEndpoints
{
    public static IEndpointRouteBuilder MapEventBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var eventScoped = builder.MapGroup("/events/{eventId:guid}/bookings");
        eventScoped.MapPost("/", Create);

        builder.MapGet("/event-bookings", ListMyBookings);
        builder.MapGet("/event-bookings/{bookingId:guid}", GetById);
        builder.MapPost("/event-bookings/{bookingId:guid}/payment-confirmation", ConfirmPayment);
        builder.MapDelete("/event-bookings/{bookingId:guid}", Cancel);

        return builder;
    }

    /// <summary>
    /// The caller's own event-ticket bookings ("my booked events") — slim
    /// summary cards with the joined event so the mobile screen can render
    /// without a follow-up fetch. Booker identity on Event.EventBookings is
    /// free text (no FK to any user table), so we match on the caller's
    /// Firebase email claim — a provider only ever sees bookings they made.
    /// Mirror of the pet-parent host's GET /pet-parents/{petParentId}/event-bookings.
    /// </summary>
    private static async Task<IResult> ListMyBookings(
        HttpContext httpContext,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var email = httpContext.User.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email))
        {
            return ApiResults.Forbidden(
                "EmailClaimMissing",
                "The Firebase token does not carry an email claim, so the booking list cannot be resolved.");
        }

        var summaries = await bookingService.ListByBookerEmailAsync(email, cancellationToken);
        return ApiResults.Ok(summaries.Select(ToSummaryResponse).ToArray());
    }

    private static EventBookingSummaryResponse ToSummaryResponse(EventBookingSummary summary) =>
        new(
            summary.BookingId,
            summary.EventId,
            summary.EventTitle,
            summary.EventCategory,
            summary.EventType,
            summary.EventStartDate,
            summary.EventStartTime,
            summary.EventBannerImageUrl,
            summary.EventLocation is null
                ? null
                : new EventLocationResponse(
                    summary.EventLocation.HouseNumber,
                    summary.EventLocation.Street,
                    summary.EventLocation.City,
                    summary.EventLocation.Zip,
                    summary.EventLocation.Country,
                    summary.EventLocation.Latitude,
                    summary.EventLocation.Longitude),
            summary.BookerName,
            summary.BookerEmail,
            summary.BookerMobile,
            summary.TicketCount,
            summary.PaymentMethod,
            summary.PaymentStatus,
            summary.PaymentReference,
            summary.TotalAmount,
            summary.Status,
            summary.CreatedAtUtc,
            summary.UpdatedAtUtc,
            summary.CancelledAtUtc);

    private static async Task<IResult> Create(
        Guid eventId,
        CreateEventBookingRequest request,
        HttpContext httpContext,
        IEventBookingService bookingService,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        // Resolve the caller's own ProviderId so the service can block a
        // provider from booking tickets to an event they organise. Null when
        // the caller has no provider profile (then they can't be the organiser).
        var callerProviderId = await ResolveCallerProviderId(
            httpContext, onboardingService, cancellationToken);

        try
        {
            var result = await bookingService.CreateAsync(
                new CreateEventBookingCommand(
                    eventId,
                    request.BookerName,
                    request.BookerEmail,
                    request.BookerMobile,
                    request.AttendeeNames,
                    request.PaymentMethod,
                    callerProviderId),
                cancellationToken);

            return ApiResults.Created($"/api/v1/event-bookings/{result.BookingId}", ToResponse(result));
        }
        catch (EventBookingEventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (EventBookingSelfBookingNotAllowedException exception)
        {
            return ApiResults.Forbidden("SelfBookingNotAllowed", exception.Message);
        }
        catch (EventBookingNotPhysicalException exception)
        {
            return ApiResults.BadRequest("EventNotBookable", exception.Message);
        }
        catch (EventBookingOnlineSingleTicketException exception)
        {
            return ApiResults.BadRequest("OnlineEventSingleTicket", exception.Message);
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

    private static async Task<Guid?> ResolveCallerProviderId(
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var me = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);
            return me.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            // No provider auth identity for this Firebase user → the caller
            // can't be any event's organiser, so there's nothing to block.
            return null;
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

    private static async Task<IResult> Cancel(
        Guid bookingId,
        HttpContext httpContext,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // Booker identity on Event.EventBookings is free text (no FK to any
        // user table), so we authorise the cancel by matching the caller's
        // Firebase email claim against the booking's BookerEmail — the booker
        // can only cancel a booking they made.
        var email = httpContext.User.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email))
        {
            return ApiResults.Forbidden(
                "EmailClaimMissing",
                "The Firebase token does not carry an email claim, so the booking cannot be cancelled.");
        }

        try
        {
            var result = await bookingService.CancelByBookerAsync(bookingId, email, cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (EventBookingNotFoundForBookerException exception)
        {
            return ApiResults.NotFound("EventBookingNotFound", exception.Message);
        }
        catch (EventBookingAlreadyCancelledException exception)
        {
            return ApiResults.Conflict("EventBookingAlreadyCancelled", exception.Message);
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

using Pawfront.Application.Events;
using Pawfront.Contracts.Events;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Organiser-only dashboard reads for an event. Every endpoint here checks
/// that the event in the URL belongs to the provider in the URL, returning
/// 404 EventNotFound if it doesn't (we don't distinguish "doesn't exist"
/// vs "not yours" to avoid leaking existence).
/// </summary>
internal static class EventDashboardEndpoints
{
    public static IEndpointRouteBuilder MapEventDashboardEndpoints(this IEndpointRouteBuilder builder)
    {
        var dashboard = builder.MapGroup("/providers/{providerId:guid}/events/{eventId:guid}");
        dashboard.MapGet("/attendees", ListAttendees);
        dashboard.MapGet("/metrics", GetMetrics);
        return builder;
    }

    private static async Task<IResult> ListAttendees(
        Guid providerId,
        Guid eventId,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var attendees = await bookingService.ListAttendeesAsync(providerId, eventId, cancellationToken);
            var payload = new EventAttendeeResponse[attendees.Count];
            for (var i = 0; i < attendees.Count; i++)
            {
                var a = attendees[i];
                payload[i] = new EventAttendeeResponse(
                    a.TicketId,
                    a.BookingId,
                    a.TicketNumber,
                    a.AttendeeName,
                    a.BookerName,
                    a.BookerEmail,
                    a.BookerMobile,
                    a.PaymentMethod,
                    a.PaymentStatus,
                    a.TotalAmount,
                    a.CreatedAtUtc);
            }
            return ApiResults.Ok(payload);
        }
        catch (EventNotFoundForProviderException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
    }

    private static async Task<IResult> GetMetrics(
        Guid providerId,
        Guid eventId,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var metrics = await bookingService.GetMetricsAsync(providerId, eventId, cancellationToken);
            return ApiResults.Ok(new EventMetricsResponse(
                metrics.Views,
                metrics.Shares,
                metrics.Inquiries,
                metrics.ConfirmedAttendees,
                metrics.Earnings));
        }
        catch (EventNotFoundForProviderException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
    }
}

using System.Security.Claims;
using Pawfront.Application.Events;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Common;
using Pawfront.Contracts.Events;
using Pawfront.Contracts.Services.PetSitter;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Event endpoints for the pet-parent host. Two groups:
///  - Catalog reads + public counters (provider-agnostic, mirror of the
///    provider host) under <c>/events</c>.
///  - Parent-organised event creation under
///    <c>/pet-parents/{petParentId}/events</c>, ownership-filtered so a
///    parent can only create events under their own id.
///
/// Organiser dashboard endpoints (attendees / metrics) are NOT exposed for
/// parents.
/// </summary>
internal static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder builder)
    {
        // Catalog-wide listing with optional filters. Anchored before the
        // {eventId:guid} route so the literal /events GET resolves to List.
        builder.MapGet("/events", List);
        // Trending events (top-N by engagement). The literal "trending" segment
        // can't match the {eventId:guid} route below, but keep it ahead for clarity.
        builder.MapGet("/events/trending", Trending);
        builder.MapGet("/events/{eventId:guid}", GetById);

        // Public engagement counters — any signed-in pet parent can call
        // these from the event detail / share / contact-organiser UI.
        var eventScoped = builder.MapGroup("/events/{eventId:guid}");
        eventScoped.MapPost("/views", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.View, svc, ct));
        eventScoped.MapPost("/shares", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.Share, svc, ct));
        eventScoped.MapPost("/inquiries", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.Inquiry, svc, ct));

        // Organiser sets how they want ticket proceeds paid out (Cash/Digital).
        eventScoped.MapPost("/payout-methods", SavePayoutMethods);

        // Parent-organised event creation. Ownership-filtered: the
        // {petParentId} route segment must match the caller's resolved id.
        var parentScoped = builder
            .MapGroup("/pet-parents/{petParentId:guid}/events")
            .RequireOwnedPetParent();
        parentScoped.MapPost("/banner-image", UploadBanner).DisableAntiforgery();
        parentScoped.MapPost("/", Create);
        parentScoped.MapGet("/", ListByPetParent);
        parentScoped.MapPut("/{eventId:guid}", Update);
        parentScoped.MapPatch("/{eventId:guid}", Patch);

        return builder;
    }

    private static async Task<IResult> UploadBanner(
        Guid petParentId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.EventBanner,
            petParentId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        return ApiResults.Ok(new UploadImageResponse(url));
    }

    private static async Task<IResult> Create(
        Guid petParentId,
        CreateEventRequest request,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await eventService.CreateByParentAsync(
                new CreateParentEventCommand(
                    petParentId,
                    request.EventCategory,
                    request.IsChildFriendly,
                    request.Title,
                    request.Description,
                    request.BannerImageUrl,
                    request.Amenities,
                    request.EventType,
                    request.StartDate,
                    request.EndDate,
                    request.StartTime,
                    request.EndTime,
                    request.IsPaid,
                    request.Price,
                    request.CancellationPolicy,
                    request.EventLink,
                    request.Physical is null
                        ? null
                        : new PhysicalEventInput(
                            request.Physical.MaximumCapacity,
                            ToLocationInput(request.Physical.Location))),
                cancellationToken);

            return ApiResults.Created($"/api/v1/events/{result.EventId}", ToResponse(result));
        }
        catch (EventPetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> Update(
        Guid petParentId,
        Guid eventId,
        UpdateEventRequest request,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await eventService.UpdateByParentAsync(
                new UpdateParentEventCommand(
                    eventId,
                    petParentId,
                    request.EventCategory,
                    request.IsChildFriendly,
                    request.Title,
                    request.Description,
                    request.BannerImageUrl,
                    request.Amenities,
                    request.EventType,
                    request.StartDate,
                    request.EndDate,
                    request.StartTime,
                    request.EndTime,
                    request.IsPaid,
                    request.Price,
                    request.CancellationPolicy,
                    request.EventLink,
                    request.Physical is null
                        ? null
                        : new PhysicalEventInput(
                            request.Physical.MaximumCapacity,
                            ToLocationInput(request.Physical.Location))),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (EventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    /// <summary>
    /// Partial edit of a parent-organised event. Reads the current event,
    /// overlays only the fields present in the body, then runs the same
    /// full-replace update path so every cross-field invariant is re-validated.
    /// Ownership is enforced by the group filter + the sproc's parent check.
    /// </summary>
    private static async Task<IResult> Patch(
        Guid petParentId,
        Guid eventId,
        PatchEventRequest request,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        var current = await eventService.GetAsync(eventId, cancellationToken);
        if (current is null)
        {
            return ApiResults.NotFound("EventNotFound", $"Event '{eventId}' was not found.");
        }

        try
        {
            var result = await eventService.UpdateByParentAsync(
                new UpdateParentEventCommand(
                    eventId,
                    petParentId,
                    request.EventCategory.Or(current.EventCategory),
                    request.IsChildFriendly.Or(current.IsChildFriendly),
                    request.Title.Or(current.Title),
                    request.Description.Or(current.Description),
                    request.BannerImageUrl.Or(current.BannerImageUrl),
                    request.Amenities.Or(current.Amenities),
                    request.EventType.Or(current.EventType),
                    request.StartDate.Or(current.StartDate),
                    request.EndDate.Or(current.EndDate),
                    request.StartTime.Or(current.StartTime),
                    request.EndTime.Or(current.EndTime),
                    request.IsPaid.Or(current.IsPaid),
                    request.Price.Or(current.Price),
                    request.CancellationPolicy.Or(current.CancellationPolicy),
                    request.EventLink.Or(current.EventLink),
                    MergePhysical(request.Physical, current.Physical)),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (EventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListByPetParent(
        Guid petParentId,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        var results = await eventService.ListByPetParentAsync(petParentId, cancellationToken);
        // Organiser's own events — IsBookable defaults true (not a booking surface).
        return ApiResults.Ok(results.Select(e => ToResponse(e)).ToArray());
    }

    private static async Task<IResult> List(
        string? eventCategory,
        string? eventType,
        DateOnly? startDate,
        DateOnly? endDate,
        bool? isChildFriendly,
        // Repeated query: ?amenities=Restrooms&amenities=FreeParking. ASP.NET
        // model-binds repeated scalars into a string[] for minimal APIs.
        string[]? amenities,
        // Optional free-text title search (case-insensitive "contains").
        string? title,
        HttpContext httpContext,
        IEventService eventService,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await eventService.ListAsync(
                new EventListFilter(
                    eventCategory,
                    eventType,
                    startDate,
                    endDate,
                    isChildFriendly,
                    amenities,
                    title),
                cancellationToken);

            // An event the caller already holds tickets for is no longer
            // bookable. Booker identity is the caller's Firebase email claim.
            var bookedEventIds = await bookingService.ListBookedEventIdsByBookerEmailAsync(
                httpContext.User.FindFirstValue("email"), cancellationToken);

            return ApiResults.Ok(results
                .Select(r => ToResponse(r, !bookedEventIds.Contains(r.EventId)))
                .ToArray());
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetById(
        Guid eventId,
        HttpContext httpContext,
        IEventService eventService,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var result = await eventService.GetAsync(eventId, cancellationToken);
        if (result is null)
        {
            return ApiResults.NotFound("EventNotFound", $"Event '{eventId}' was not found.");
        }

        var bookedEventIds = await bookingService.ListBookedEventIdsByBookerEmailAsync(
            httpContext.User.FindFirstValue("email"), cancellationToken);
        return ApiResults.Ok(ToResponse(result, !bookedEventIds.Contains(result.EventId)));
    }

    private static async Task<IResult> Trending(
        int? take,
        HttpContext httpContext,
        IEventService eventService,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // Default to 20; the sproc clamps to 1..100.
        var results = await eventService.ListTrendingAsync(take ?? 20, cancellationToken);

        // An event the caller already holds tickets for is no longer bookable.
        // Booker identity is the caller's Firebase email claim.
        var bookedEventIds = await bookingService.ListBookedEventIdsByBookerEmailAsync(
            httpContext.User.FindFirstValue("email"), cancellationToken);

        return ApiResults.Ok(results
            .Select(r => ToResponse(r, !bookedEventIds.Contains(r.EventId)))
            .ToArray());
    }

    private static async Task<IResult> SavePayoutMethods(
        Guid eventId,
        SaveEventPayoutMethodsRequest request,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await eventService.SavePayoutMethodsAsync(
                eventId, request?.PayoutMethods!, cancellationToken);
            return ApiResults.Ok(new EventPayoutMethodsResponse(result.EventId, result.PayoutMethods));
        }
        catch (EventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (EventNotPaidException exception)
        {
            return ApiResults.BadRequest("FreeEventNoPayout", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> IncrementCounter(
        Guid eventId,
        string counterType,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        try
        {
            var counters = await eventService.IncrementCounterAsync(eventId, counterType, cancellationToken);
            return ApiResults.Ok(new EventCountersResponse(
                counters.ViewCount,
                counters.ShareCount,
                counters.InquiryCount));
        }
        catch (EventNotFoundException exception)
        {
            return ApiResults.NotFound("EventNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static EventResponse ToResponse(EventResult result, bool isBookable = true)
    {
        return new EventResponse(
            result.EventId,
            result.ProviderId,
            result.PetParentId,
            result.EventCategory,
            result.IsChildFriendly,
            result.Title,
            result.Description,
            result.BannerImageUrl,
            result.Amenities,
            result.EventType,
            result.StartDate,
            result.EndDate,
            result.StartTime,
            result.EndTime,
            result.IsPaid,
            result.Price,
            result.CancellationPolicy,
            result.EventLink,
            result.Physical is null
                ? null
                : new PhysicalEventResponse(
                    result.Physical.MaximumCapacity,
                    ToLocationResponse(result.Physical.Location)),
            new PawPrintsResponse(
                result.Counters.ViewCount,
                result.Counters.ShareCount,
                result.Counters.InquiryCount),
            new EventOrganizerResponse(
                result.Organizer.Type,
                result.Organizer.Id,
                result.Organizer.Name,
                result.Organizer.ImageUrl),
            new EventBookingStatsResponse(
                // "Max bookings" = the physical venue capacity; null/unlimited
                // for online events.
                result.Physical?.MaximumCapacity,
                result.TotalBookings),
            isBookable,
            result.PaymentOptions,
            result.Attendees?
                .Select(a => new EventAttendeeSummaryResponse(a.AttendeeName, a.TicketNumber))
                .ToArray(),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }

    /// <summary>
    /// Resolves the physical block for a PATCH: the supplied block when present
    /// (null clears it), otherwise the event's current physical details mapped
    /// back to input shape. Validation later drops it if the merged event is
    /// online, or requires it if the merged event is physical.
    /// </summary>
    private static PhysicalEventInput? MergePhysical(
        Optional<PhysicalEventRequest?> patched,
        PhysicalEventResult? current)
    {
        if (patched.IsSet)
        {
            return patched.Value is null
                ? null
                : new PhysicalEventInput(
                    patched.Value.MaximumCapacity,
                    ToLocationInput(patched.Value.Location));
        }

        return current is null
            ? null
            : new PhysicalEventInput(current.MaximumCapacity, ToLocationInput(current.Location));
    }

    private static EventLocationInput? ToLocationInput(EventLocationRequest? location)
    {
        return location is null
            ? null
            : new EventLocationInput(
                location.HouseNumber,
                location.Street,
                location.City,
                location.Zip,
                location.Country,
                location.Latitude,
                location.Longitude);
    }

    private static EventLocationInput? ToLocationInput(EventLocationResult? location)
    {
        return location is null
            ? null
            : new EventLocationInput(
                location.HouseNumber,
                location.Street,
                location.City,
                location.Zip,
                location.Country,
                location.Latitude,
                location.Longitude);
    }

    private static EventLocationResponse? ToLocationResponse(EventLocationResult? location)
    {
        return location is null
            ? null
            : new EventLocationResponse(
                location.HouseNumber,
                location.Street,
                location.City,
                location.Zip,
                location.Country,
                location.Latitude,
                location.Longitude);
    }
}

using Pawfront.Application.Events;
using Pawfront.Application.Storage;
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

        // Parent-organised event creation. Ownership-filtered: the
        // {petParentId} route segment must match the caller's resolved id.
        var parentScoped = builder
            .MapGroup("/pet-parents/{petParentId:guid}/events")
            .RequireOwnedPetParent();
        parentScoped.MapPost("/banner-image", UploadBanner).DisableAntiforgery();
        parentScoped.MapPost("/", Create);

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
                    request.Physical is null
                        ? null
                        : new PhysicalEventInput(
                            request.Physical.MaximumCapacity,
                            request.Physical.IsPaid,
                            request.Physical.Price)),
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

    private static async Task<IResult> List(
        string? eventCategory,
        string? eventType,
        DateOnly? startDate,
        DateOnly? endDate,
        bool? isChildFriendly,
        // Repeated query: ?amenities=Restrooms&amenities=FreeParking. ASP.NET
        // model-binds repeated scalars into a string[] for minimal APIs.
        string[]? amenities,
        IEventService eventService,
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
                    amenities),
                cancellationToken);

            return ApiResults.Ok(results.Select(ToResponse).ToArray());
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetById(
        Guid eventId,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        var result = await eventService.GetAsync(eventId, cancellationToken);
        return result is null
            ? ApiResults.NotFound("EventNotFound", $"Event '{eventId}' was not found.")
            : ApiResults.Ok(ToResponse(result));
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

    private static EventResponse ToResponse(EventResult result)
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
            result.Physical is null
                ? null
                : new PhysicalEventResponse(
                    result.Physical.MaximumCapacity,
                    result.Physical.IsPaid,
                    result.Physical.Price),
            new PawPrintsResponse(
                result.Counters.ViewCount,
                result.Counters.ShareCount,
                result.Counters.InquiryCount),
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }
}

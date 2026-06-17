using Pawfront.Application.Events;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Events;
using Pawfront.Contracts.Services.PetSitter;

namespace Pawfront.Api.Endpoints;

internal static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder builder)
    {
        var providerScoped = builder.MapGroup("/providers/{providerId:guid}/events");

        providerScoped.MapPost("/banner-image", UploadBanner).DisableAntiforgery();
        providerScoped.MapPost("/", Create);
        providerScoped.MapGet("/", ListByProvider);

        // Catalog-wide listing with optional filters. Anchored before the
        // {eventId:guid} route so a literal /events GET resolves to List.
        builder.MapGet("/events", List);
        builder.MapGet("/events/{eventId:guid}", GetById);

        // Public engagement counters — anyone signed in can call these from
        // the event detail / share / contact-organiser UI.
        var eventScoped = builder.MapGroup("/events/{eventId:guid}");
        eventScoped.MapPost("/views", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.View, svc, ct));
        eventScoped.MapPost("/shares", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.Share, svc, ct));
        eventScoped.MapPost("/inquiries", (Guid eventId, IEventService svc, CancellationToken ct)
            => IncrementCounter(eventId, EventCounterType.Inquiry, svc, ct));

        // Organiser sets how they want ticket proceeds paid out (Cash/Digital).
        eventScoped.MapPost("/payout-methods", SavePayoutMethods);

        return builder;
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

    private static async Task<IResult> UploadBanner(
        Guid providerId,
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
            providerId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        return ApiResults.Ok(new UploadImageResponse(url));
    }

    private static async Task<IResult> Create(
        Guid providerId,
        CreateEventRequest request,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await eventService.CreateAsync(
                new CreateEventCommand(
                    providerId,
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
                    request.Physical is null
                        ? null
                        : new PhysicalEventInput(
                            request.Physical.MaximumCapacity,
                            ToLocationInput(request.Physical.Location))),
                cancellationToken);

            return ApiResults.Created($"/api/v1/events/{result.EventId}", ToResponse(result));
        }
        catch (EventProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId,
        IEventService eventService,
        CancellationToken cancellationToken)
    {
        var results = await eventService.ListByProviderAsync(providerId, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
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
            result.IsPaid,
            result.Price,
            result.CancellationPolicy,
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
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
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

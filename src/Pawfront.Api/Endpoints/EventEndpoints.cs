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

        builder.MapGet("/events/{eventId:guid}", GetById);

        return builder;
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
                    request.Physical is null
                        ? null
                        : new PhysicalEventInput(
                            request.Physical.MaximumCapacity,
                            request.Physical.IsPaid,
                            request.Physical.Price)),
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
            result.CreatedAtUtc,
            result.UpdatedAtUtc);
    }
}

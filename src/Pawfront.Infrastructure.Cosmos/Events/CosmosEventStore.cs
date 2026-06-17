using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Events;
using Pawfront.Infrastructure.Cosmos.Documents;

namespace Pawfront.Infrastructure.Cosmos.Events;

internal sealed class CosmosEventStore(
    IEventsContainerAccessor containerAccessor) : IEventCosmosStore
{
    public async Task<PhysicalEventResult> UpsertPhysicalAsync(
        Guid eventId,
        string eventCategory,
        PhysicalEventInput details,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var existing = await TryReadAsync(container, eventId, eventCategory, cancellationToken);

        var document = new EventDocument
        {
            Id = eventId.ToString(),
            EventId = eventId.ToString(),
            EventCategory = eventCategory,
            Physical = new PhysicalEventDetails
            {
                MaximumCapacity = details.MaximumCapacity,
                Location = ToLocationDocument(details.Location)
            },
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };

        await container.UpsertItemAsync(
            document,
            new PartitionKey(eventCategory),
            cancellationToken: cancellationToken);

        return new PhysicalEventResult(
            document.Physical.MaximumCapacity,
            ToLocationResult(document.Physical.Location));
    }

    public async Task<PhysicalEventResult?> GetPhysicalAsync(
        Guid eventId,
        string eventCategory,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, eventId, eventCategory, cancellationToken);

        return document?.Physical is null
            ? null
            : new PhysicalEventResult(
                document.Physical.MaximumCapacity,
                ToLocationResult(document.Physical.Location));
    }

    private static EventLocationDetails? ToLocationDocument(EventLocationInput? location)
    {
        if (location is null)
        {
            return null;
        }

        return new EventLocationDetails
        {
            HouseNumber = location.HouseNumber,
            Street = location.Street,
            City = location.City,
            Zip = location.Zip,
            Country = location.Country,
            Latitude = location.Latitude,
            Longitude = location.Longitude
        };
    }

    private static EventLocationResult? ToLocationResult(EventLocationDetails? location)
    {
        if (location is null)
        {
            return null;
        }

        return new EventLocationResult(
            location.HouseNumber,
            location.Street,
            location.City,
            location.Zip,
            location.Country,
            location.Latitude,
            location.Longitude);
    }

    private static async Task<EventDocument?> TryReadAsync(
        Container container,
        Guid eventId,
        string eventCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<EventDocument>(
                eventId.ToString(),
                new PartitionKey(eventCategory),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

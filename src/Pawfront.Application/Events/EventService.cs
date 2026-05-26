using Microsoft.Extensions.Logging;
using Pawfront.Domain.Events;

namespace Pawfront.Application.Events;

internal sealed class EventService(
    IEventSqlStore sqlStore,
    IEventCosmosStore cosmosStore,
    ILogger<EventService> logger) : IEventService
{
    private static readonly IReadOnlySet<string> AllowedCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(EventCategory.AdoptionAndRescue),
        nameof(EventCategory.PetTraining),
        nameof(EventCategory.Charity),
        nameof(EventCategory.Volunteering),
        nameof(EventCategory.HealthAndWellness),
        nameof(EventCategory.SocialAndCultural),
        nameof(EventCategory.OutdoorActivities),
        nameof(EventCategory.ParentEducation)
    };

    private static readonly IReadOnlySet<string> AllowedEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(EventType.Physical),
        nameof(EventType.Online)
    };

    private static readonly IReadOnlySet<string> AllowedAmenities = new HashSet<string>(StringComparer.Ordinal)
    {
        "FreeParking", "PaidParking", "Restrooms", "DrinkingWater",
        "FoodAndBeverage", "SeatingAreas", "FirstAidBooth", "None"
    };

    public async Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken)
    {
        // Validate enum-shaped fields up front so SQL never sees garbage.
        var category = NormalizeOne(command.EventCategory, AllowedCategories, nameof(command.EventCategory));
        var eventType = NormalizeOne(command.EventType, AllowedEventTypes, nameof(command.EventType));
        var amenities = NormalizeAmenities(command.Amenities);
        var title = Required(command.Title, nameof(command.Title));
        var description = Required(command.Description, nameof(command.Description));

        if (command.EndDate < command.StartDate)
        {
            throw new ArgumentException("EndDate must be on or after StartDate.", nameof(command.EndDate));
        }

        PhysicalEventInput? physicalInput = null;
        if (eventType == nameof(EventType.Physical))
        {
            if (command.Physical is null)
            {
                throw new ArgumentException(
                    "Physical events require capacity and ticketing details.",
                    nameof(command.Physical));
            }
            physicalInput = ValidatePhysical(command.Physical);
        }

        // Write SQL first (source of truth + EventId generation).
        var snapshot = await sqlStore.CreateAsync(
            new CreateEventSqlInput(
                command.ProviderId,
                category,
                command.IsChildFriendly,
                title,
                description,
                Trim(command.BannerImageUrl),
                amenities,
                eventType,
                command.StartDate,
                command.EndDate,
                command.StartTime,
                command.EndTime),
            cancellationToken);

        // For physical events, write the Cosmos extension doc.
        PhysicalEventResult? physicalResult = null;
        if (physicalInput is not null)
        {
            try
            {
                physicalResult = await cosmosStore.UpsertPhysicalAsync(
                    snapshot.EventId,
                    category,
                    physicalInput,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Event {EventId} was created in SQL but the Cosmos extension write failed. The event will be returned without physical details.",
                    snapshot.EventId);
            }
        }

        return ToResult(snapshot, physicalResult);
    }

    public async Task<EventResult?> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var snapshot = await sqlStore.GetAsync(eventId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        PhysicalEventResult? physical = null;
        if (string.Equals(snapshot.EventType, nameof(EventType.Physical), StringComparison.Ordinal))
        {
            physical = await cosmosStore.GetPhysicalAsync(eventId, snapshot.EventCategory, cancellationToken);
        }

        return ToResult(snapshot, physical);
    }

    public async Task<IReadOnlyCollection<EventResult>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        var snapshots = await sqlStore.ListByProviderAsync(providerId, cancellationToken);
        // Listing skips Cosmos for cost; detail endpoint hydrates physical details.
        return snapshots.Select(s => ToResult(s, physical: null)).ToArray();
    }

    public Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken)
    {
        var normalised = NormaliseCounterType(counterType);
        return sqlStore.IncrementCounterAsync(eventId, normalised, cancellationToken);
    }

    private static string NormaliseCounterType(string? counterType)
    {
        var trimmed = counterType?.Trim();
        if (trimmed is not null
            && (string.Equals(trimmed, EventCounterType.View, StringComparison.Ordinal)
                || string.Equals(trimmed, EventCounterType.Share, StringComparison.Ordinal)
                || string.Equals(trimmed, EventCounterType.Inquiry, StringComparison.Ordinal)))
        {
            return trimmed;
        }

        throw new ArgumentException(
            $"CounterType '{counterType}' is not supported. Use View, Share, or Inquiry.",
            nameof(counterType));
    }

    private static EventResult ToResult(EventSqlSnapshot snapshot, PhysicalEventResult? physical)
    {
        return new EventResult(
            snapshot.EventId,
            snapshot.ProviderId,
            snapshot.EventCategory,
            snapshot.IsChildFriendly,
            snapshot.Title,
            snapshot.Description,
            snapshot.BannerImageUrl,
            snapshot.Amenities,
            snapshot.EventType,
            snapshot.StartDate,
            snapshot.EndDate,
            snapshot.StartTime,
            snapshot.EndTime,
            physical,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc);
    }

    private static PhysicalEventInput ValidatePhysical(PhysicalEventInput input)
    {
        if (input.MaximumCapacity < 1)
        {
            throw new ArgumentException(
                "Physical.MaximumCapacity must be at least 1.",
                nameof(input.MaximumCapacity));
        }

        if (input.IsPaid)
        {
            if (input.Price is null || input.Price < 0)
            {
                throw new ArgumentException(
                    "Physical.Price must be a non-negative value when IsPaid is true.",
                    nameof(input.Price));
            }
            return input;
        }

        // Free ticket: Price is ignored. Normalise to null for storage clarity.
        return input with { Price = null };
    }

    private static List<string> NormalizeAmenities(IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            return new List<string>();
        }

        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in values)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!AllowedAmenities.Contains(trimmed))
            {
                throw new ArgumentException(
                    $"Amenities contains unsupported value '{trimmed}'.",
                    nameof(values));
            }

            if (seen.Add(trimmed))
            {
                deduped.Add(trimmed);
            }
        }

        if (deduped.Contains("None") && deduped.Count > 1)
        {
            throw new ArgumentException(
                "Amenities cannot mix 'None' with any other value.",
                nameof(values));
        }

        return deduped;
    }

    private static string NormalizeOne(string? value, IReadOnlySet<string> allowed, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        if (!allowed.Contains(trimmed))
        {
            throw new ArgumentException($"{fieldName} value '{trimmed}' is not supported.", fieldName);
        }

        return trimmed;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
        return value.Trim();
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

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

    private static readonly IReadOnlySet<string> AllowedCancellationPolicies = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(EventCancellationPolicy.FullRefundUpTo4Hours),
        nameof(EventCancellationPolicy.FullRefundUpTo2Hours),
        nameof(EventCancellationPolicy.NoRefund)
    };

    private const string PayoutMethodCash = "Cash";
    private const string PayoutMethodDigital = "Digital";

    private static readonly IReadOnlySet<string> AllowedPayoutMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        PayoutMethodCash, PayoutMethodDigital
    };

    private static readonly IReadOnlySet<string> AllowedAmenities = new HashSet<string>(StringComparer.Ordinal)
    {
        "FreeParking", "PaidParking", "Restrooms", "DrinkingWater",
        "FoodAndBeverage", "SeatingAreas", "FirstAidBooth", "None"
    };

    public async Task<EventResult> CreateAsync(CreateEventCommand command, CancellationToken cancellationToken)
    {
        var validated = ValidateForCreate(
            command.EventCategory, command.EventType, command.Amenities,
            command.Title, command.Description, command.StartDate, command.EndDate,
            command.IsPaid, command.Price, command.CancellationPolicy, command.Physical);

        // Write SQL first (source of truth + EventId generation). Ticketing
        // (IsPaid / Price) lives in SQL so it's returned for every event type,
        // including online events that have no Cosmos extension document.
        var snapshot = await sqlStore.CreateAsync(
            new CreateEventSqlInput(
                command.ProviderId,
                validated.Category,
                command.IsChildFriendly,
                validated.Title,
                validated.Description,
                Trim(command.BannerImageUrl),
                validated.Amenities,
                validated.EventType,
                command.StartDate,
                command.EndDate,
                command.StartTime,
                command.EndTime,
                validated.IsPaid,
                validated.Price,
                validated.CancellationPolicy),
            cancellationToken);

        var physicalResult = await WritePhysicalExtensionAsync(
            snapshot.EventId, validated.Category, validated.PhysicalInput, cancellationToken);

        return ToResult(snapshot, physicalResult);
    }

    public async Task<EventResult> CreateByParentAsync(
        CreateParentEventCommand command,
        CancellationToken cancellationToken)
    {
        var validated = ValidateForCreate(
            command.EventCategory, command.EventType, command.Amenities,
            command.Title, command.Description, command.StartDate, command.EndDate,
            command.IsPaid, command.Price, command.CancellationPolicy, command.Physical);

        var snapshot = await sqlStore.CreateByParentAsync(
            new CreateParentEventSqlInput(
                command.PetParentId,
                validated.Category,
                command.IsChildFriendly,
                validated.Title,
                validated.Description,
                Trim(command.BannerImageUrl),
                validated.Amenities,
                validated.EventType,
                command.StartDate,
                command.EndDate,
                command.StartTime,
                command.EndTime,
                validated.IsPaid,
                validated.Price,
                validated.CancellationPolicy),
            cancellationToken);

        var physicalResult = await WritePhysicalExtensionAsync(
            snapshot.EventId, validated.Category, validated.PhysicalInput, cancellationToken);

        return ToResult(snapshot, physicalResult);
    }

    /// <summary>
    /// Shared validation for both the provider- and parent-create paths.
    /// Normalises enum-shaped fields, checks the date window, validates the
    /// top-level ticketing (applies to every event type), and validates the
    /// physical block (capacity) for Physical events.
    /// </summary>
    private static ValidatedEventCreate ValidateForCreate(
        string eventCategory,
        string eventType,
        IReadOnlyCollection<string> amenities,
        string title,
        string description,
        DateOnly startDate,
        DateOnly endDate,
        bool isPaid,
        decimal? price,
        string cancellationPolicy,
        PhysicalEventInput? physical)
    {
        var category = NormalizeOne(eventCategory, AllowedCategories, nameof(eventCategory));
        var normalisedType = NormalizeOne(eventType, AllowedEventTypes, nameof(eventType));
        var normalisedAmenities = NormalizeAmenities(amenities);
        var normalisedTitle = Required(title, nameof(title));
        var normalisedDescription = Required(description, nameof(description));
        var normalisedCancellationPolicy = NormalizeOne(
            cancellationPolicy, AllowedCancellationPolicies, nameof(cancellationPolicy));

        if (endDate < startDate)
        {
            throw new ArgumentException("EndDate must be on or after StartDate.", nameof(endDate));
        }

        // Ticketing is validated for every event type — online events can be
        // paid too. A free event ignores any submitted price (normalised null).
        var (validatedIsPaid, validatedPrice) = ValidateTicketing(isPaid, price);

        PhysicalEventInput? physicalInput = null;
        if (normalisedType == nameof(EventType.Physical))
        {
            if (physical is null)
            {
                throw new ArgumentException(
                    "Physical events require capacity details.",
                    nameof(physical));
            }
            physicalInput = ValidatePhysical(physical);

            // Amenities are mandatory for physical events (use "None" to mean
            // "no amenities"). Online events have no venue, so they may omit
            // them entirely.
            if (normalisedAmenities.Count == 0)
            {
                throw new ArgumentException(
                    "Physical events require at least one amenity (use 'None' if there are none).",
                    nameof(amenities));
            }
        }

        return new ValidatedEventCreate(
            category, normalisedType, normalisedAmenities,
            normalisedTitle, normalisedDescription,
            validatedIsPaid, validatedPrice, normalisedCancellationPolicy, physicalInput);
    }

    private async Task<PhysicalEventResult?> WritePhysicalExtensionAsync(
        Guid eventId,
        string category,
        PhysicalEventInput? physicalInput,
        CancellationToken cancellationToken)
    {
        if (physicalInput is null)
        {
            return null;
        }

        try
        {
            return await cosmosStore.UpsertPhysicalAsync(eventId, category, physicalInput, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Event {EventId} was created in SQL but the Cosmos extension write failed. The event will be returned without physical details.",
                eventId);
            return null;
        }
    }

    private sealed record ValidatedEventCreate(
        string Category,
        string EventType,
        IReadOnlyCollection<string> Amenities,
        string Title,
        string Description,
        bool IsPaid,
        decimal? Price,
        string CancellationPolicy,
        PhysicalEventInput? PhysicalInput);

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

    public async Task<IReadOnlyCollection<EventResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        var snapshots = await sqlStore.ListByPetParentAsync(petParentId, cancellationToken);
        // Listing skips Cosmos for cost; detail endpoint hydrates physical details.
        return snapshots.Select(s => ToResult(s, physical: null)).ToArray();
    }

    public async Task<IReadOnlyCollection<EventResult>> ListAsync(
        EventListFilter filter,
        CancellationToken cancellationToken)
    {
        var normalised = ValidateFilter(filter);
        var snapshots = await sqlStore.ListAsync(normalised, cancellationToken);
        // Listing skips Cosmos for cost; detail endpoint hydrates physical details.
        return snapshots.Select(s => ToResult(s, physical: null)).ToArray();
    }

    private static EventListFilter ValidateFilter(EventListFilter filter)
    {
        string? category = null;
        if (!string.IsNullOrWhiteSpace(filter.EventCategory))
        {
            category = NormalizeOne(filter.EventCategory, AllowedCategories, nameof(filter.EventCategory));
        }

        string? eventType = null;
        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            eventType = NormalizeOne(filter.EventType, AllowedEventTypes, nameof(filter.EventType));
        }

        if (filter.StartDate is not null && filter.EndDate is not null && filter.EndDate < filter.StartDate)
        {
            throw new ArgumentException(
                "EndDate must be on or after StartDate.",
                nameof(filter.EndDate));
        }

        IReadOnlyCollection<string>? amenities = null;
        if (filter.Amenities is { Count: > 0 })
        {
            // NormalizeAmenities throws on unknown values / 'None' mixed with others.
            amenities = NormalizeAmenities(filter.Amenities);
        }

        return new EventListFilter(
            category,
            eventType,
            filter.StartDate,
            filter.EndDate,
            filter.IsChildFriendly,
            amenities);
    }

    public Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken)
    {
        var normalised = NormaliseCounterType(counterType);
        return sqlStore.IncrementCounterAsync(eventId, normalised, cancellationToken);
    }

    public async Task<EventPayoutMethodsResult> SavePayoutMethodsAsync(
        Guid eventId,
        IReadOnlyCollection<string> payoutMethods,
        CancellationToken cancellationToken)
    {
        var (acceptsCash, acceptsDigital) = NormalizePayoutMethods(payoutMethods);

        // The store's sproc is authoritative for the not-found / free-event
        // checks (it reads Event.Events.IsPaid race-safely).
        var saved = await sqlStore.SavePayoutMethodsAsync(
            eventId, acceptsCash, acceptsDigital, cancellationToken);

        return new EventPayoutMethodsResult(eventId, saved);
    }

    private static (bool AcceptsCash, bool AcceptsDigital) NormalizePayoutMethods(
        IReadOnlyCollection<string>? payoutMethods)
    {
        if (payoutMethods is null)
        {
            throw new ArgumentException("Payout methods are required.", nameof(payoutMethods));
        }

        var acceptsCash = false;
        var acceptsDigital = false;

        foreach (var raw in payoutMethods)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!AllowedPayoutMethods.Contains(trimmed))
            {
                throw new ArgumentException(
                    $"Payout method '{trimmed}' is not supported. Use Cash or Digital.",
                    nameof(payoutMethods));
            }

            if (trimmed == PayoutMethodCash) acceptsCash = true;
            if (trimmed == PayoutMethodDigital) acceptsDigital = true;
        }

        if (!acceptsCash && !acceptsDigital)
        {
            throw new ArgumentException(
                "At least one payout method must be selected.",
                nameof(payoutMethods));
        }

        return (acceptsCash, acceptsDigital);
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
            snapshot.PetParentId,
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
            snapshot.IsPaid,
            snapshot.Price,
            snapshot.CancellationPolicy,
            physical,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.Counters,
            BuildOrganizer(snapshot));
    }

    /// <summary>
    /// Resolves the organiser block from the snapshot. Exactly one of
    /// ProviderId / PetParentId is set (DB CHECK constraint); ProviderId wins
    /// the type discriminator when present. Name / image are joined in the
    /// sproc — image is always null for provider organisers.
    /// </summary>
    private static EventOrganizer BuildOrganizer(EventSqlSnapshot snapshot)
    {
        return snapshot.ProviderId is Guid providerId
            ? new EventOrganizer("Provider", providerId, snapshot.OrganizerName, snapshot.OrganizerImageUrl)
            : new EventOrganizer(
                "PetParent",
                snapshot.PetParentId ?? Guid.Empty,
                snapshot.OrganizerName,
                snapshot.OrganizerImageUrl);
    }

    private static (bool IsPaid, decimal? Price) ValidateTicketing(bool isPaid, decimal? price)
    {
        if (isPaid)
        {
            if (price is null || price < 0)
            {
                throw new ArgumentException(
                    "Price must be a non-negative value when IsPaid is true.",
                    nameof(price));
            }
            return (true, price);
        }

        // Free ticket: Price is ignored. Normalise to null for storage clarity.
        return (false, null);
    }

    private static PhysicalEventInput ValidatePhysical(PhysicalEventInput input)
    {
        if (input.MaximumCapacity < 1)
        {
            throw new ArgumentException(
                "Physical.MaximumCapacity must be at least 1.",
                nameof(input.MaximumCapacity));
        }

        return input with { Location = ValidateLocation(input.Location) };
    }

    private static EventLocationInput ValidateLocation(EventLocationInput? location)
    {
        if (location is null)
        {
            throw new ArgumentException(
                "Physical events require a location.",
                nameof(location));
        }

        // House number / street / city / zip / country are mandatory; lat/long
        // are optional. Trim the required fields and normalise nothing else.
        return new EventLocationInput(
            HouseNumber: Required(location.HouseNumber, "Location.HouseNumber"),
            Street: Required(location.Street, "Location.Street"),
            City: Required(location.City, "Location.City"),
            Zip: Required(location.Zip, "Location.Zip"),
            Country: Required(location.Country, "Location.Country"),
            Latitude: location.Latitude,
            Longitude: location.Longitude);
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

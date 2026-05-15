namespace Pawfront.Application.Events;

public sealed record CreateEventCommand(
    Guid ProviderId,
    string EventCategory,
    bool IsChildFriendly,
    string Title,
    string Description,
    string? BannerImageUrl,
    IReadOnlyCollection<string> Amenities,
    string EventType,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    PhysicalEventInput? Physical);

public sealed record PhysicalEventInput(
    int MaximumCapacity,
    bool IsPaid,
    decimal? Price);

public sealed record EventResult(
    Guid EventId,
    Guid ProviderId,
    string EventCategory,
    bool IsChildFriendly,
    string Title,
    string Description,
    string? BannerImageUrl,
    IReadOnlyCollection<string> Amenities,
    string EventType,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    PhysicalEventResult? Physical,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PhysicalEventResult(
    int MaximumCapacity,
    bool IsPaid,
    decimal? Price);

public sealed record EventSqlSnapshot(
    Guid EventId,
    Guid ProviderId,
    string EventCategory,
    bool IsChildFriendly,
    string Title,
    string Description,
    string? BannerImageUrl,
    IReadOnlyCollection<string> Amenities,
    string EventType,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateEventSqlInput(
    Guid ProviderId,
    string EventCategory,
    bool IsChildFriendly,
    string Title,
    string Description,
    string? BannerImageUrl,
    IReadOnlyCollection<string> Amenities,
    string EventType,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed class EventProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

public sealed class EventNotFoundException(Guid eventId)
    : Exception($"Event '{eventId}' was not found.");

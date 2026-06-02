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

/// <summary>
/// Catalog-wide event listing filter. Every field is optional; omitting a field
/// means "no constraint on that dimension". <see cref="StartDate"/> and
/// <see cref="EndDate"/> form an overlap window — an event is returned when
/// its <c>[StartDate, EndDate]</c> intersects the requested window.
/// <see cref="Amenities"/>, when supplied, requires the event to carry EVERY
/// listed amenity (ALL-match semantics).
/// </summary>
public sealed record EventListFilter(
    string? EventCategory,
    string? EventType,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool? IsChildFriendly,
    IReadOnlyCollection<string>? Amenities);

public sealed class EventProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

public sealed class EventNotFoundException(Guid eventId)
    : Exception($"Event '{eventId}' was not found.");

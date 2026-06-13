namespace Pawfront.Contracts.Events;

public sealed record CreateEventRequest(
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
    PhysicalEventRequest? Physical);

public sealed record PhysicalEventRequest(
    int MaximumCapacity,
    bool IsPaid,
    decimal? Price);

public sealed record EventResponse(
    Guid EventId,
    // Exactly one of ProviderId / PetParentId is non-null, identifying the
    // organiser. Provider-created events have ProviderId set; parent-created
    // events have PetParentId set.
    Guid? ProviderId,
    Guid? PetParentId,
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
    PhysicalEventResponse? Physical,
    // Engagement counters (views / shares / inquiries) for this event.
    PawPrintsResponse PawPrints,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PhysicalEventResponse(
    int MaximumCapacity,
    bool IsPaid,
    decimal? Price);

/// <summary>
/// Engagement counters for an event ("PawPrints"): how many times it was
/// viewed, shared, and inquired about. Bumped via the public counter endpoints
/// (<c>POST /events/{eventId}/{views,shares,inquiries}</c>) and surfaced
/// read-only on the event detail/list responses.
/// </summary>
public sealed record PawPrintsResponse(
    int ViewCount,
    int ShareCount,
    int InquiryCount);

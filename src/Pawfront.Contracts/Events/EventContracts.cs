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
    // Ticketing lives at the top level so it applies to every event type —
    // online events have no `physical` block but can still be paid. When
    // IsPaid is true, Price is required (>= 0); otherwise Price is ignored.
    bool IsPaid,
    decimal? Price,
    // Refund policy the creator advertises. One of: FullRefundUpTo4Hours,
    // FullRefundUpTo2Hours, NoRefund. Required for every event type.
    string CancellationPolicy,
    PhysicalEventRequest? Physical);

public sealed record PhysicalEventRequest(
    int MaximumCapacity,
    // The venue address. Required for physical events; online events have no
    // `physical` block, so this never applies to them.
    EventLocationRequest Location);

/// <summary>
/// Venue address for a physical event. HouseNumber / Street / City / Zip /
/// Country are required; Latitude / Longitude are optional.
/// </summary>
public sealed record EventLocationRequest(
    string HouseNumber,
    string Street,
    string City,
    string Zip,
    string Country,
    double? Latitude,
    double? Longitude);

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
    // Ticketing is returned for every event type (physical AND online), so it
    // sits on the main object rather than inside the physical-only block.
    bool IsPaid,
    decimal? Price,
    // Refund policy (FullRefundUpTo4Hours | FullRefundUpTo2Hours | NoRefund).
    string CancellationPolicy,
    PhysicalEventResponse? Physical,
    // Engagement counters (views / shares / inquiries) for this event.
    PawPrintsResponse PawPrints,
    // Who created the event — a provider OR a pet parent. Resolved from
    // whichever of ProviderId / PetParentId is set.
    EventOrganizerResponse Organizer,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Identifies the organiser of an event for display in event reads. An event
/// is created by either a provider or a pet parent; <see cref="Type"/> says
/// which (<c>Provider</c> | <c>PetParent</c>) and <see cref="Id"/> is the
/// matching ProviderId / PetParentId. <see cref="Name"/> is the organiser's
/// display name; <see cref="ImageUrl"/> is their profile photo (populated for
/// pet-parent organisers; null for providers, which have no profile photo).
/// </summary>
public sealed record EventOrganizerResponse(
    string Type,
    Guid Id,
    string? Name,
    string? ImageUrl);

public sealed record PhysicalEventResponse(
    int MaximumCapacity,
    // Nullable for resilience against legacy physical events created before
    // location was captured; always populated for events created since.
    EventLocationResponse? Location);

public sealed record EventLocationResponse(
    string HouseNumber,
    string Street,
    string City,
    string Zip,
    string Country,
    double? Latitude,
    double? Longitude);

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

/// <summary>
/// Body for <c>POST /events/{eventId}/payout-methods</c>. How the event
/// organiser wants ticket proceeds paid out — one or more of Cash / Digital.
/// Only valid for paid events.
/// </summary>
public sealed record SaveEventPayoutMethodsRequest(
    IReadOnlyCollection<string> PayoutMethods);

public sealed record EventPayoutMethodsResponse(
    Guid EventId,
    IReadOnlyCollection<string> PayoutMethods);

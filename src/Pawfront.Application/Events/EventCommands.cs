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
    // Ticketing applies to every event type, so it sits at the top level
    // rather than inside the physical-only block.
    bool IsPaid,
    decimal? Price,
    // Refund policy the creator advertises. Optional (null when unset) — it
    // doesn't apply to free events, so it's never required.
    string? CancellationPolicy,
    // Joining link for ONLINE events (online-only; null for physical events).
    string? EventLink,
    PhysicalEventInput? Physical);

/// <summary>
/// Parent-organised event creation input. Same shape as
/// <see cref="CreateEventCommand"/> but keyed by <see cref="PetParentId"/>;
/// the underlying row goes into <c>Event.Events</c> with the
/// <c>PetParentId</c> column set and <c>ProviderId</c> NULL.
/// </summary>
public sealed record CreateParentEventCommand(
    Guid PetParentId,
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    // Joining link for ONLINE events (online-only; null for physical events).
    string? EventLink,
    PhysicalEventInput? Physical);

/// <summary>
/// Full-replace edit of a provider-organised event (every field is editable).
/// Same shape as <see cref="CreateEventCommand"/> plus the target
/// <see cref="EventId"/>. The owning provider is taken from the authenticated
/// route, never the body.
/// </summary>
public sealed record UpdateEventCommand(
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    // Joining link for ONLINE events (online-only; null for physical events).
    string? EventLink,
    PhysicalEventInput? Physical);

/// <summary>
/// Full-replace edit of a parent-organised event. Mirror of
/// <see cref="UpdateEventCommand"/> keyed by <see cref="PetParentId"/>.
/// </summary>
public sealed record UpdateParentEventCommand(
    Guid EventId,
    Guid PetParentId,
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    // Joining link for ONLINE events (online-only; null for physical events).
    string? EventLink,
    PhysicalEventInput? Physical);

public sealed record PhysicalEventInput(
    int MaximumCapacity,
    // Nullable on the raw input — the service validates that physical events
    // actually carry a location (and its required sub-fields).
    EventLocationInput? Location);

public sealed record EventLocationInput(
    string HouseNumber,
    string Street,
    string City,
    string Zip,
    string Country,
    double? Latitude,
    double? Longitude);

public sealed record EventResult(
    Guid EventId,
    // Exactly one of ProviderId / PetParentId is non-null. The Cosmos
    // physical-event extension document is keyed by EventId only, so
    // booking + counter flows don't care which organiser created it.
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
    // Ticketing is surfaced for every event type (physical AND online).
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    // Joining link for online events (null for physical events).
    string? EventLink,
    PhysicalEventResult? Physical,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    // The view / share / inquiry engagement counters ("PawPrints" on the wire).
    EventCounters Counters,
    // Who organised the event (provider or pet parent), derived from whichever
    // of ProviderId / PetParentId is set plus the joined name / photo.
    EventOrganizer Organizer,
    // Total tickets booked (non-cancelled) so far. The "max number of bookings"
    // is the physical capacity (Physical.MaximumCapacity); null/unlimited for
    // online events.
    int TotalBookings = 0,
    // Detail-only (populated by GetAsync / UpdateAsync): the event's payment
    // options (payout methods) and attendee names. Null on list reads.
    IReadOnlyCollection<string>? PaymentOptions = null,
    IReadOnlyCollection<EventAttendeeSummary>? Attendees = null);

/// <summary>
/// A single attendee on an event, as surfaced on the public event-detail read.
/// Deliberately names-only (plus the ticket number) — booker contact / payment
/// detail stays on the organiser-only dashboard.
/// </summary>
public sealed record EventAttendeeSummary(
    string AttendeeName,
    int TicketNumber);

/// <summary>
/// Display details for the event's organiser. <see cref="Type"/> is
/// <c>Provider</c> or <c>PetParent</c>, <see cref="Id"/> the matching id.
/// <see cref="Name"/> / <see cref="ImageUrl"/> come from the organiser's
/// profile row (image is null for providers — they have no profile photo).
/// </summary>
public sealed record EventOrganizer(
    string Type,
    Guid Id,
    string? Name,
    string? ImageUrl,
    // Total events this organiser has created. Populated on the event-detail
    // read (GET /events/{eventId}); null on list reads.
    int? TotalEventsOrganized = null);

public sealed record PhysicalEventResult(
    int MaximumCapacity,
    // Nullable for resilience against legacy docs that predate location.
    EventLocationResult? Location);

public sealed record EventLocationResult(
    string HouseNumber,
    string Street,
    string City,
    string Zip,
    string Country,
    double? Latitude,
    double? Longitude);

public sealed record EventSqlSnapshot(
    Guid EventId,
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    // Joining link for online events (null for physical events). Read from the
    // last column of result set 1 in every event-returning sproc.
    string? EventLink,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    EventCounters Counters,
    // Organiser display fields, joined from Provider.Providers /
    // Parent.PetParents in every event-returning sproc's result set 1.
    // OrganizerImageUrl is null for provider-organised events.
    string? OrganizerName = null,
    string? OrganizerImageUrl = null,
    // Total tickets booked (non-cancelled) — appended after the organiser
    // fields in every event-returning sproc's result set 1.
    int TotalBookings = 0,
    // Detail-only: filled by GetAsync / UpdateAsync from GetEvent/UpdateEvent's
    // extra result sets (payout methods + attendee names). Null on list reads.
    IReadOnlyCollection<string>? PaymentOptions = null,
    IReadOnlyCollection<EventAttendeeSummary>? Attendees = null);

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
    TimeOnly EndTime,
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    string? EventLink);

/// <summary>
/// Wire shape sent from the parent-side event service to the SQL store.
/// Mirrors <see cref="CreateEventSqlInput"/> but keyed by
/// <see cref="PetParentId"/>.
/// </summary>
public sealed record CreateParentEventSqlInput(
    Guid PetParentId,
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    string? EventLink);

/// <summary>
/// Wire shape sent from the event service to the SQL store for a provider
/// event edit. Mirrors <see cref="CreateEventSqlInput"/> plus the target
/// <see cref="EventId"/> (and ProviderId used for the ownership check).
/// </summary>
public sealed record UpdateEventSqlInput(
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    string? EventLink);

/// <summary>
/// Wire shape for a parent event edit. Mirror of
/// <see cref="UpdateEventSqlInput"/> keyed by <see cref="PetParentId"/>.
/// </summary>
public sealed record UpdateParentEventSqlInput(
    Guid EventId,
    Guid PetParentId,
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
    bool IsPaid,
    decimal? Price,
    string? CancellationPolicy,
    string? EventLink);

/// <summary>
/// Catalog-wide event listing filter. Every field is optional; omitting a field
/// means "no constraint on that dimension". <see cref="StartDate"/> and
/// <see cref="EndDate"/> form an overlap window — an event is returned when
/// its <c>[StartDate, EndDate]</c> intersects the requested window.
/// <see cref="Amenities"/>, when supplied, requires the event to carry EVERY
/// listed amenity (ALL-match semantics). <see cref="Title"/>, when supplied,
/// is a case-insensitive "contains" match on the event title.
/// </summary>
public sealed record EventListFilter(
    string? EventCategory,
    string? EventType,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool? IsChildFriendly,
    IReadOnlyCollection<string>? Amenities,
    string? Title = null);

public sealed class EventProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

public sealed class EventPetParentNotFoundException(Guid petParentId)
    : Exception($"Pet parent '{petParentId}' was not found.");

public sealed class EventNotFoundException(Guid eventId)
    : Exception($"Event '{eventId}' was not found.");

/// <summary>
/// Thrown when a payout method is set on a free event. Payout methods only
/// apply to paid events (free events collect no money to pay out).
/// </summary>
public sealed class EventNotPaidException(Guid eventId)
    : Exception($"Event '{eventId}' is free — a payout method only applies to paid events.");

public sealed record EventPayoutMethodsResult(
    Guid EventId,
    IReadOnlyCollection<string> PayoutMethods);

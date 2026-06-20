using Pawfront.Contracts.Common;

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

/// <summary>
/// Body for the full-replace event edit (<c>PUT /…/events/{eventId}</c>) on
/// both hosts. Same shape as <see cref="CreateEventRequest"/> — every field is
/// editable. The organiser (provider / pet parent) and the event id come from
/// the route, not the body.
/// </summary>
public sealed record UpdateEventRequest(
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
    string CancellationPolicy,
    PhysicalEventRequest? Physical);

/// <summary>
/// Body for the partial event edit (<c>PATCH /…/events/{eventId}</c>) on both
/// hosts. Every field is wrapped in <see cref="Optional{T}"/> so the server can
/// tell an omitted field (leave unchanged) apart from one explicitly sent as
/// null (clear it). Only the supplied fields are applied; everything else keeps
/// its current value. The merged result is then validated exactly like a full
/// edit, so cross-field rules still hold (e.g. flipping <c>eventType</c> to
/// Physical requires a <c>physical</c> block; flipping <c>isPaid</c> to true
/// requires a <c>price</c>).
/// </summary>
public sealed record PatchEventRequest(
    Optional<string> EventCategory,
    Optional<bool> IsChildFriendly,
    Optional<string> Title,
    Optional<string> Description,
    // Nullable + clearable: send null to remove the banner, omit to keep it.
    Optional<string?> BannerImageUrl,
    Optional<IReadOnlyCollection<string>> Amenities,
    Optional<string> EventType,
    Optional<DateOnly> StartDate,
    Optional<DateOnly> EndDate,
    Optional<TimeOnly> StartTime,
    Optional<TimeOnly> EndTime,
    Optional<bool> IsPaid,
    // Nullable: null is meaningful only when the merged event is free.
    Optional<decimal?> Price,
    Optional<string> CancellationPolicy,
    // Send the whole physical block to change capacity/venue; null to drop it
    // (only valid when the merged event is online); omit to keep it.
    Optional<PhysicalEventRequest?> Physical);

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
    // Booking summary: max bookings (= physical capacity; null/unlimited for
    // online events) and total bookings done so far. Returned on list + detail.
    EventBookingStatsResponse Bookings,
    // Detail-only (GET /events/{eventId}, PUT edit): the event's payment options
    // (Cash / Digital). Null on list reads.
    IReadOnlyCollection<string>? PaymentOptions,
    // Detail-only: who is attending — names + ticket number only. Null on list
    // reads.
    IReadOnlyCollection<EventAttendeeSummaryResponse>? Attendees,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Booking summary for an event. <see cref="MaxBookings"/> is the maximum
/// number of bookings allowed (the physical venue capacity); it is null for
/// online events, which have no capacity limit. <see cref="TotalBookings"/> is
/// the number of tickets booked (non-cancelled) so far.
/// </summary>
public sealed record EventBookingStatsResponse(
    int? MaxBookings,
    int TotalBookings);

/// <summary>
/// One attendee on an event, as exposed on the public event-detail read —
/// names only (no booker contact or payment detail; those stay on the
/// organiser-only dashboard).
/// </summary>
public sealed record EventAttendeeSummaryResponse(
    string AttendeeName,
    int TicketNumber);

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

namespace Pawfront.Contracts.Bookings;

/// <summary>
/// A pet parent's "my bookings" card for a single-day service booking, grouped
/// into sections: the booking itself, the booked provider's details, the
/// service details (including the per-hour price), plus the frozen-at-creation
/// cancellation-policy and selected-location blocks (same section shapes the
/// booking-detail read uses). Returned by
/// <c>GET /pet-parents/{petParentId}/bookings</c>.
/// </summary>
public sealed record ParentServiceBookingCardResponse(
    BookingResponse Booking,
    BookingProviderDetailsSection ProviderDetails,
    BookingServiceDetailsSection ServiceDetails,
    // The cancellation policy frozen onto the booking at creation — a later
    // provider policy change never re-rules this card. Null hours = no restriction.
    CancellationPolicyDetailsSection CancellationPolicy,
    // The selected-location address frozen onto the booking at creation. Address
    // fields are null on legacy rows without a snapshot (the booking-detail read
    // is the live-fallback authority) and on Custom walk-ins.
    BookingLocationDetailsSection Location,
    // The caller's own review of this booking — see the section's own docs for
    // why canReview differs from the booking detail's.
    ParentBookingReviewSection Review);

/// <summary>
/// A pet parent's "my bookings" card for a multi-night (NightStay) booking.
/// Same provider section as the single-day card; the service section carries the
/// per-night price instead of per-hour, plus the same frozen cancellation-policy
/// and selected-location blocks. Returned by
/// <c>GET /pet-parents/{petParentId}/night-stay-bookings</c>.
/// </summary>
public sealed record ParentNightStayBookingCardResponse(
    NightStayBookingResponse Booking,
    BookingProviderDetailsSection ProviderDetails,
    NightStayServiceDetailsSection ServiceDetails,
    // The cancellation policy frozen onto the stay at creation.
    CancellationPolicyDetailsSection CancellationPolicy,
    // The selected-location address frozen onto the stay at creation; null
    // address fields on legacy rows without a snapshot.
    BookingLocationDetailsSection Location,
    ParentBookingReviewSection Review);

/// <summary>
/// The caller's own review of a booking on their "my bookings" list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CanReview"/> means "show the review prompt", which is deliberately a
/// DIFFERENT question from the booking detail's <c>review.canReview</c> — that one
/// stays true after a review exists, because a review can be edited. On a list the
/// useful signal is whether anything is still outstanding, so this goes false as
/// soon as <see cref="Rating"/> is populated. Read the two together:
/// <c>true / null</c> = ask them to review it, <c>false / 4</c> = they gave it
/// four stars, <c>false / null</c> = the booking is not reviewable (yet).
/// </para>
/// <para>
/// A booking becomes reviewable at COMPLETED or PAID. To edit an existing review,
/// or to see its comment and photos, go to the booking detail — only the score
/// travels here.
/// </para>
/// </remarks>
public sealed record ParentBookingReviewSection(bool CanReview, int? Rating);

/// <summary>
/// The booked provider's display details. <see cref="BusinessName"/> is the
/// business name for businesses, or the provider's personal name for freelancers
/// (whose offering doc has no business name); it is null only when neither can be
/// resolved (e.g. the offering was removed). <see cref="ImageUrl"/> /
/// <see cref="City"/> come from the provider's offering document and are null when
/// unresolved; category + sub-category come from the booking row and are always present.
/// </summary>
public sealed record BookingProviderDetailsSection(
    Guid ProviderId,
    string? BusinessName,
    string? ImageUrl,
    string? City,
    string ServiceCategory,
    string SubCategory);

/// <summary>
/// The booked service's details for a single-day booking. <see cref="PricePerHour"/>
/// is the unit rate frozen onto the booking at creation (price-lock; the menu-item
/// price for PetGroomer), falling back to the live offering rate only for legacy
/// rows without a snapshot; null when neither can be resolved.
/// <see cref="Description"/> is what the provider says about the booked service —
/// the menu item's blurb for a groomer, the session description for a trainer.
/// Unlike the price it is read LIVE (cosmetic copy, deliberately not frozen onto
/// the booking), so a later edit by the provider shows through. Null for the
/// other categories and when the offering can't be resolved.
/// </summary>
public sealed record BookingServiceDetailsSection(
    Guid ServiceId,
    string? ServiceType,
    string? ServiceItemCode,
    decimal? PricePerHour,
    string? Description);

/// <summary>
/// The booked service's details for a night-stay booking. <see cref="PricePerNight"/>
/// is the per-night rate frozen onto the stay at creation (price-lock), falling back
/// to the live offering rate only for legacy rows; null when neither resolves.
/// </summary>
public sealed record NightStayServiceDetailsSection(
    Guid ServiceId,
    string ServiceType,
    decimal? PricePerNight);

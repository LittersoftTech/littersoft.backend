namespace Pawfront.Contracts.Bookings;

/// <summary>
/// A pet parent's "my bookings" card for a single-day service booking, grouped
/// into sections: the booking itself, the booked provider's details, and the
/// service details (including the per-hour price). Returned by
/// <c>GET /pet-parents/{petParentId}/bookings</c>.
/// </summary>
public sealed record ParentServiceBookingCardResponse(
    BookingResponse Booking,
    BookingProviderDetailsSection ProviderDetails,
    BookingServiceDetailsSection ServiceDetails);

/// <summary>
/// A pet parent's "my bookings" card for a multi-night (NightStay) booking.
/// Same provider section as the single-day card; the service section carries the
/// per-night price instead of per-hour. Returned by
/// <c>GET /pet-parents/{petParentId}/night-stay-bookings</c>.
/// </summary>
public sealed record ParentNightStayBookingCardResponse(
    NightStayBookingResponse Booking,
    BookingProviderDetailsSection ProviderDetails,
    NightStayServiceDetailsSection ServiceDetails);

/// <summary>
/// The booked provider's display details. <see cref="BusinessName"/> /
/// <see cref="ImageUrl"/> / <see cref="City"/> come from the provider's offering
/// document and are null when unresolved (e.g. the offering was removed);
/// category + sub-category come from the booking row and are always present.
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
/// is the offering's unit rate (the menu-item price for PetGroomer); null when the
/// offering can't be resolved.
/// </summary>
public sealed record BookingServiceDetailsSection(
    Guid ServiceId,
    string? ServiceType,
    string? ServiceItemCode,
    decimal? PricePerHour);

/// <summary>
/// The booked service's details for a night-stay booking. <see cref="PricePerNight"/>
/// is the NightStay offering's per-night rate; null when unresolved.
/// </summary>
public sealed record NightStayServiceDetailsSection(
    Guid ServiceId,
    string ServiceType,
    decimal? PricePerNight);

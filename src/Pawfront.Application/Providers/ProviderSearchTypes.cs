namespace Pawfront.Application.Providers;

/// <summary>
/// Criteria for the per-service parent-facing search endpoints. All filters
/// are optional; endpoint-level validation guarantees the date/time fields
/// arrive as complete groups (trio for day care, pair for night stay).
/// Animals / City / ServiceLocation share the semantics of
/// <see cref="ProviderDiscoveryFilter"/>.
/// </summary>
public sealed record DayCareProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    int Skip,
    int Take);

/// <param name="StartDate">Drop-off date (first stayed night).</param>
/// <param name="PickupDate">
/// Checkout date — NOT a stayed night. Every date from StartDate up to the
/// day before PickupDate must have free NightStay capacity.
/// </param>
public sealed record NightStayProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? StartDate,
    DateOnly? PickupDate,
    int Skip,
    int Take);

/// <param name="ServiceItemCode">
/// One of the 18 canonical grooming codes. When set, only providers with
/// that item active on their menu match, and Charges carries the item's
/// price. When null, any groomer with at least one active menu item matches
/// and Charges is null (price is per item).
/// </param>
public sealed record GroomingProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    string? ServiceItemCode,
    int Skip,
    int Take);

public sealed record VetProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    int Skip,
    int Take);

/// <summary>
/// Criteria for the PetTrainer/TrainingSession search. A training session is a
/// single fixed-duration booking (like a vet appointment): when a date is set,
/// a provider matches if any free slot of the session's duration exists that
/// day. Charges = PricePerSession.
/// </summary>
public sealed record TrainerProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    int Skip,
    int Take);

/// <summary>
/// Per-provider search hit. ServiceId is included so the mobile client can
/// go straight to the slots / booking endpoints without a follow-up lookup.
/// </summary>
/// <param name="BusinessName">
/// Shop/hotel/clinic name; null for freelance sub-categories (mobile renders
/// e.g. "Freelance Pet Sitter").
/// </param>
/// <param name="CompletedBookings">
/// Bookings already finished (not cancelled / no-show) across ALL the
/// provider's services.
/// </param>
/// <param name="Charges">
/// PricePerHour for DayCare/NightStay, the menu item's price for a grooming
/// search with ServiceItemCode, PricePerAppointment for vets. Null for a
/// grooming search without a ServiceItemCode.
/// </param>
/// <param name="ChargesUnit">PerHour | PerService | PerAppointment.</param>
/// <param name="ImageUrl">
/// The service image the provider uploaded for this offering (the same image
/// shown on the discovery card). Null when the provider hasn't set one.
/// </param>
public sealed record ProviderSearchResult(
    Guid ProviderId,
    Guid ServiceId,
    string SubCategory,
    string? BusinessName,
    int CompletedBookings,
    decimal? Charges,
    string ChargesUnit,
    string? ServiceItemCode,
    string? ImageUrl);

public static class ProviderSearchChargesUnits
{
    public const string PerHour = "PerHour";
    public const string PerService = "PerService";
    public const string PerAppointment = "PerAppointment";
    public const string PerSession = "PerSession";
}

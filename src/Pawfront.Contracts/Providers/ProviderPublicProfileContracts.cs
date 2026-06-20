using Pawfront.Contracts.Services.PetAdoptionSale;
using Pawfront.Contracts.Services.PetGroomer;
using Pawfront.Contracts.Services.PetSitter;
using Pawfront.Contracts.Services.PetTrainer;
using Pawfront.Contracts.Services.Vet;

namespace Pawfront.Contracts.Providers;

/// <summary>
/// Parent-facing public profile of a provider — returned by
/// <c>GET /api/v1/providers/{providerId}</c> on the pet-parent host.
/// Exactly one of the per-category fields is populated, matching
/// <see cref="ServiceCategory"/>. Provider personal info (name, mobile,
/// DOB) is intentionally omitted.
/// </summary>
public sealed record ProviderPublicProfileResponse(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyList<ProviderWorkingHoursDayResponse> WorkingHours,
    IReadOnlyList<ProviderTimeOffEntryResponse> TimeOff,
    // Booking policy the provider advertises. MinimumHoursBeforeCancellation
    // is null when the provider has set no cancellation policy.
    // AcceptedPaymentMethods is the provider's payout-method set (Cash /
    // Digital); empty when none configured.
    int? MinimumHoursBeforeCancellation,
    IReadOnlyCollection<string> AcceptedPaymentMethods,
    // Parent reviews of the provider. Always empty for now — the review
    // feature isn't built yet; the field is wired so the mobile client can
    // bind it ahead of time.
    IReadOnlyList<ProviderReviewResponse> Reviews,
    PetSitterServiceResponse? PetSitter,
    PetGroomerServiceResponse? PetGroomer,
    PetTrainerServiceResponse? PetTrainer,
    PetAdoptionSaleServiceResponse? PetAdoptionSale,
    VetServiceResponse? Vet);

/// <summary>
/// Placeholder for a parent's review of a provider. The review feature is not
/// built yet, so this carries no fields and the <c>Reviews</c> array is always
/// empty — fields will be added when reviews land.
/// </summary>
public sealed record ProviderReviewResponse();

public sealed record ProviderWorkingHoursDayResponse(
    int DayOfWeek,              // 0 = Sunday .. 6 = Saturday
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);

public sealed record ProviderTimeOffEntryResponse(
    Guid ClosureId,
    Guid ServiceId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

/// <summary>
/// Slim per-provider card returned by <c>GET /api/v1/providers</c> on the
/// pet-parent host. Mobile drills into <c>GET /providers/{providerId}</c>
/// for the full offering / working hours / time off shape.
/// </summary>
public sealed record ProviderSummaryResponse(
    Guid ProviderId,
    string ServiceCategory,
    string SubCategory,
    string? DisplayName,
    string? ImageUrl,
    string City,
    string? About,
    IReadOnlyCollection<string> AnimalsHandled);

/// <summary>
/// Per-provider hit returned by the four per-service booking-search
/// endpoints (<c>GET /providers/search/day-care|night-stay|groomers|vets</c>)
/// on the pet-parent host. ServiceId lets mobile jump straight to the
/// slots / booking endpoints.
/// </summary>
/// <param name="BusinessName">Null for freelance sub-categories.</param>
/// <param name="CompletedBookings">Bookings already served (not cancelled/no-show) across all the provider's services.</param>
/// <param name="Charges">Null for a grooming search without a serviceItemCode (prices are per menu item).</param>
/// <param name="ChargesUnit"><c>PerHour | PerService | PerAppointment</c>.</param>
public sealed record ProviderSearchResultResponse(
    Guid ProviderId,
    Guid ServiceId,
    string SubCategory,
    string? BusinessName,
    int CompletedBookings,
    decimal? Charges,
    string ChargesUnit,
    string? ServiceItemCode,
    // The service image the provider uploaded for this offering (same image
    // as the discovery card's). Null when the provider hasn't set one.
    string? ImageUrl);

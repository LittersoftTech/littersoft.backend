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
    PetSitterServiceResponse? PetSitter,
    PetGroomerServiceResponse? PetGroomer,
    PetTrainerServiceResponse? PetTrainer,
    PetAdoptionSaleServiceResponse? PetAdoptionSale,
    VetServiceResponse? Vet);

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

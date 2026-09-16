namespace Pawfront.Contracts.Jobs;

/// <summary>
/// One page of <c>GET /api/v1/providers/{providerId}/jobs</c> — the provider's
/// agenda / "jobs to review" inbox.
/// </summary>
/// <param name="Statuses">
/// The RAW lifecycle statuses the filter expanded to, echoed back so a client can
/// see exactly what a friendly group like <c>Accepted</c> resolved to. Empty when
/// no status filter was applied, which means every status.
/// </param>
/// <param name="TotalCount">Matching jobs before paging.</param>
public sealed record ProviderJobsResponse(
    IReadOnlyCollection<string> Statuses,
    string SortBy,
    string SortDirection,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore,
    IReadOnlyCollection<ProviderJobResponse> Jobs);

/// <summary>
/// One job card. Carries everything the list renders, so a page of jobs costs one
/// request rather than a booking-detail call per row.
/// </summary>
/// <param name="BookingType">
/// <c>SingleDay</c> | <c>NightStay</c> — which detail endpoint to open. The two
/// booking tables share no id space, so <paramref name="BookingId"/> alone cannot
/// say.
/// </param>
/// <param name="JobId">The friendly <c>PF-000123</c> label, the same one the booking detail shows.</param>
/// <param name="IsEarned">The job produced money (COMPLETED or PAID).</param>
/// <param name="IsPrivate">A Custom walk-in — the provider's own off-platform job.</param>
/// <param name="JobDate">
/// The day the job STARTS: the booking date, or a stay's check-in. Note this is
/// NOT the earnings date, which for a stay is its checkout.
/// </param>
/// <param name="CheckOutDate">Night-stay only; null on a single-day booking.</param>
/// <param name="StartTime">The booked start, or a stay's drop-off time.</param>
/// <param name="LocationType">
/// <c>ParentLocation</c> (the customer's address) or <c>ProviderLocation</c>
/// (yours). Null on legacy rows and on walk-ins that recorded neither.
/// </param>
/// <param name="Amount">
/// What the job is worth — the same figure the earnings screen reports for it.
/// Null on a legacy booking that froze no price.
/// </param>
/// <param name="Fee">
/// The Pawfront commission on it. Zero for a walk-in (off-platform), null
/// whenever <paramref name="Amount"/> is.
/// </param>
public sealed record ProviderJobResponse(
    string BookingType,
    Guid BookingId,
    string JobId,
    string Status,
    bool IsEarned,
    bool IsPaid,
    bool IsPrivate,
    Guid ServiceId,
    string? ServiceType,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly JobDate,
    DateOnly? CheckOutDate,
    int? Nights,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? LocationType,
    ProviderJobLocationResponse? Location,
    string? JobNotes,
    ProviderJobCustomerResponse Customer,
    ProviderJobPetResponse Pet,
    decimal? Amount,
    decimal? Fee,
    string? PayoutId,
    string PayoutStatus,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Where the job happens, frozen at booking time. Null when the booking recorded
/// no address snapshot — a legacy row, or a walk-in the provider typed no address
/// for.
/// </summary>
public sealed record ProviderJobLocationResponse(
    string? AddressLine,
    string? City,
    string? ZipCode);

/// <summary>
/// The customer, joined LIVE from their profile so a deleted account reads
/// "Deleted User" rather than leaving real personal data frozen in a list. On a
/// Custom walk-in the name is the free text the provider typed and everything
/// else is null — there is no parent record behind it.
/// </summary>
public sealed record ProviderJobCustomerResponse(
    Guid? PetParentId,
    string? Name,
    string? PhotoUrl);

/// <summary>
/// The pet, likewise joined live ("Deleted Pet" for a deleted one). Breed and
/// gender are null on a walk-in, which has only the free-text name and animal
/// type the provider typed.
/// </summary>
public sealed record ProviderJobPetResponse(
    Guid? PetId,
    string? Name,
    string? AnimalType,
    string? Breed,
    string? Gender);

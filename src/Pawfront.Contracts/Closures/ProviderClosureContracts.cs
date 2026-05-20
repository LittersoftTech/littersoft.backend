namespace Pawfront.Contracts.Closures;

public sealed record CreateProviderClosureRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

public enum ProviderClosureCreationStatus
{
    /// <summary>Closure was persisted; <see cref="CreateProviderClosureResponse.Closure"/> is populated.</summary>
    Created = 0,

    /// <summary>
    /// One or more confirmed bookings fall inside the requested window. The closure was
    /// NOT created; the list of conflicting bookings is returned so the UI can warn the
    /// provider and prompt them to move/cancel those bookings before retrying.
    /// </summary>
    BookingsExist = 1
}

public sealed record CreateProviderClosureResponse(
    ProviderClosureCreationStatus Status,
    ProviderClosureSummary? Closure,
    IReadOnlyList<ConflictingBookingSummary>? ConflictingBookings,
    string? WarningMessage);

public sealed record ProviderClosureSummary(
    Guid ClosureId,
    Guid ProviderId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

public sealed record ConflictingBookingSummary(
    Guid BookingId,
    Guid PetParentId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record ProviderClosuresResponse(
    Guid ProviderId,
    IReadOnlyList<ProviderClosureSummary> Closures);

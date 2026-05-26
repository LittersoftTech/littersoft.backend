namespace Pawfront.Contracts.Closures;

/// <summary>
/// Create one closure per ServiceId in <see cref="ServiceIds"/>, sharing the same
/// date range / partial-day window / reason. The batch is all-or-nothing: if any
/// service has a conflicting confirmed booking inside the window, no rows are
/// inserted and the response carries the conflict list.
/// </summary>
public sealed record CreateProviderClosureRequest(
    IReadOnlyList<Guid> ServiceIds,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

public enum ProviderClosureCreationStatus
{
    /// <summary>Closure batch was persisted; <see cref="CreateProviderClosureResponse.Closures"/> is populated.</summary>
    Created = 0,

    /// <summary>
    /// One or more confirmed bookings fall inside the requested window for one or
    /// more of the targeted services. No rows were inserted; the list of conflicting
    /// bookings (with their ServiceId) is returned so the UI can warn the provider
    /// and prompt them to move/cancel those bookings before retrying.
    /// </summary>
    BookingsExist = 1
}

public sealed record CreateProviderClosureResponse(
    ProviderClosureCreationStatus Status,
    IReadOnlyList<ProviderClosureSummary>? Closures,
    IReadOnlyList<ConflictingBookingSummary>? ConflictingBookings,
    string? WarningMessage);

public sealed record ProviderClosureSummary(
    Guid ClosureId,
    Guid ProviderId,
    Guid ServiceId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

public sealed record ConflictingBookingSummary(
    Guid ServiceId,
    Guid BookingId,
    Guid PetParentId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record ProviderClosuresResponse(
    Guid ProviderId,
    IReadOnlyList<ProviderClosureSummary> Closures);

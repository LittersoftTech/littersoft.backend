namespace Pawfront.Application.Closures;

/// <summary>
/// Create-closures input. Always carries a non-empty list of <see cref="ServiceIds"/>;
/// the server persists ONE closure row per service id in a single transaction.
/// Both <see cref="StartTime"/> and <see cref="EndTime"/> NULL = full-day closure
/// across the entire date range; setting them requires
/// <see cref="StartDate"/> == <see cref="EndDate"/> (partial-day on one specific day).
/// </summary>
public sealed record CreateProviderClosureCommand(
    Guid ProviderId,
    IReadOnlyCollection<Guid> ServiceIds,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

public sealed record ProviderClosure(
    Guid ClosureId,
    Guid ProviderId,
    Guid ServiceId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Booking that blocks one or more closures in a batch from being created. Returned
/// in the warning payload so the mobile UI can list the affected bookings (with their
/// owning service) and prompt the provider to move them before retrying.
/// </summary>
public sealed record ConflictingBooking(
    Guid ServiceId,
    Guid BookingId,
    // Null for Source = 'Custom' (provider-added private job).
    Guid? PetParentId,
    string Source,
    string? CustomerName,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

/// <summary>
/// Result of <see cref="IProviderClosureService.CreateAsync"/>. Discriminated:
/// either the closure batch was created (one row per requested ServiceId), or there
/// are existing bookings inside the window — caller surfaces the warning + list to
/// the provider. The batch is all-or-nothing: if any service has a conflict, none
/// of the rows are inserted.
/// </summary>
public abstract record CreateClosureResult
{
    private CreateClosureResult() { }

    public sealed record Created(IReadOnlyList<ProviderClosure> Closures) : CreateClosureResult;

    public sealed record BookingsExist(IReadOnlyList<ConflictingBooking> Bookings) : CreateClosureResult;
}

/// <summary>
/// A closure row that covers a given calendar date FOR A SPECIFIC SERVICE — consumed
/// by the slot service and booking service. Times are NULL for a full-day closure.
/// </summary>
public sealed record ActiveClosure(
    Guid ClosureId,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason)
{
    public bool IsFullDay => StartTime is null || EndTime is null;
}

public sealed class ProviderClosureNotFoundException(Guid closureId)
    : Exception($"Provider closure '{closureId}' was not found.");

public sealed class ProviderClosureProviderNotFoundException(Guid providerId)
    : Exception($"Provider '{providerId}' was not found.");

public sealed class ProviderClosureServiceInvalidException(Guid providerId)
    : Exception($"One or more service ids are not valid or active for provider '{providerId}'.");

public sealed class ProviderClosureEmptyServiceIdsException()
    : Exception("At least one service id is required.");

/// <summary>
/// Thrown by the booking service when the requested window overlaps an active
/// closure on the booked service (sick leave, vacation, etc.).
/// </summary>
public sealed class ProviderClosedOnDateException(Guid serviceId, DateOnly date, string? reason)
    : Exception(reason is null
        ? $"Service '{serviceId}' is closed on {date:yyyy-MM-dd}."
        : $"Service '{serviceId}' is closed on {date:yyyy-MM-dd}: {reason}");

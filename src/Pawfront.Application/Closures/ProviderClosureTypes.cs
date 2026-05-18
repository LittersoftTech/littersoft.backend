namespace Pawfront.Application.Closures;

/// <summary>
/// Create-closure input. Both <see cref="StartTime"/> and <see cref="EndTime"/> NULL
/// means a full-day closure across the entire date range; setting them requires
/// <see cref="StartDate"/> == <see cref="EndDate"/> (partial-day on one specific day).
/// </summary>
public sealed record CreateProviderClosureCommand(
    Guid ProviderId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason);

public sealed record ProviderClosure(
    Guid ClosureId,
    Guid ProviderId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Booking that blocks a closure from being created. Returned in the warning payload
/// so the mobile UI can list the affected bookings and prompt the provider to move
/// them before retrying.
/// </summary>
public sealed record ConflictingBooking(
    Guid BookingId,
    Guid PetParentId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

/// <summary>
/// Result of <see cref="IProviderClosureService.CreateAsync"/>. Discriminated:
/// either the closure was created, or there are existing bookings inside the
/// requested window — caller surfaces the warning + list to the provider.
/// </summary>
public abstract record CreateClosureResult
{
    private CreateClosureResult() { }

    public sealed record Created(ProviderClosure Closure) : CreateClosureResult;

    public sealed record BookingsExist(IReadOnlyList<ConflictingBooking> Bookings) : CreateClosureResult;
}

/// <summary>
/// A closure row that covers a given calendar date — consumed by the slot service
/// and booking service. Times are NULL for a full-day closure.
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

/// <summary>
/// Thrown by the booking service when the requested window overlaps an active
/// provider closure (sick leave, vacation, etc.).
/// </summary>
public sealed class ProviderClosedOnDateException(Guid providerId, DateOnly date, string? reason)
    : Exception(reason is null
        ? $"Provider '{providerId}' is closed on {date:yyyy-MM-dd}."
        : $"Provider '{providerId}' is closed on {date:yyyy-MM-dd}: {reason}");

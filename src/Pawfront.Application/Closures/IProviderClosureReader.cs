namespace Pawfront.Application.Closures;

/// <summary>
/// Narrow read interface consumed by the slot service and booking service so they
/// can subtract closed windows from the day's available slots and reject bookings
/// that fall inside a closure.
/// </summary>
public interface IProviderClosureReader
{
    Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken);
}

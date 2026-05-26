namespace Pawfront.Application.Closures;

/// <summary>
/// Narrow read interface consumed by the slot service and booking service so they
/// can subtract closed windows from the day's available slots and reject bookings
/// that fall inside a closure. Scoped by ServiceId — closures for one service do
/// not affect slots/bookings of another service the provider offers.
/// </summary>
public interface IProviderClosureReader
{
    Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken);
}

namespace Pawfront.Application.Closures;

/// <summary>
/// Low-level SQL operations on <c>Provider.ProviderClosures</c>. Implemented by the
/// infrastructure layer; consumed by <see cref="ProviderClosureService"/>.
/// </summary>
public interface IProviderClosureSqlStore
{
    /// <summary>
    /// Race-safe insert. Holds UPDLOCK + HOLDLOCK on the conflicting-bookings query
    /// so a concurrent CreateBooking on the same provider serialises behind it.
    /// Returns the bookings list when the window has overlapping confirmed bookings
    /// (nothing is inserted); otherwise returns the newly persisted closure.
    /// Throws <see cref="ProviderClosureProviderNotFoundException"/> when the
    /// provider profile is missing.
    /// </summary>
    Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken);

    /// <summary>Throws <see cref="ProviderClosureNotFoundException"/> when the row is missing.</summary>
    Task DeleteAsync(
        Guid providerId,
        Guid closureId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken);
}

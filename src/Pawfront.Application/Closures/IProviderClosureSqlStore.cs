namespace Pawfront.Application.Closures;

/// <summary>
/// Low-level SQL operations on <c>Provider.ProviderClosures</c>. Implemented by the
/// infrastructure layer; consumed by <see cref="ProviderClosureService"/>.
/// </summary>
public interface IProviderClosureSqlStore
{
    /// <summary>
    /// Race-safe batch insert. Validates every ServiceId belongs to the provider
    /// and is active, then runs a UPDLOCK + HOLDLOCK conflict check against the
    /// matching bookings — concurrent CreateBooking on any of the targeted services
    /// serialises behind it. Returns the conflicting bookings list when the window
    /// has overlapping confirmed bookings on ANY targeted service (nothing is
    /// inserted); otherwise returns the newly persisted closures (one per ServiceId).
    /// Throws <see cref="ProviderClosureProviderNotFoundException"/> when the
    /// provider profile is missing, or
    /// <see cref="ProviderClosureServiceInvalidException"/> when any ServiceId
    /// is unknown / inactive / not owned by the provider.
    /// </summary>
    Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        Guid? serviceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken);

    /// <summary>Throws <see cref="ProviderClosureNotFoundException"/> when the row is missing.</summary>
    Task DeleteAsync(
        Guid providerId,
        Guid closureId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken);
}

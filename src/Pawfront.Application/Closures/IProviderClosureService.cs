namespace Pawfront.Application.Closures;

public interface IProviderClosureService
{
    /// <summary>
    /// Attempt to create one closure row per service id in a single transaction.
    /// If any confirmed booking on any of the requested services falls inside the
    /// window, the call returns <see cref="CreateClosureResult.BookingsExist"/>
    /// and nothing is persisted — the provider is expected to move/cancel those
    /// bookings first and retry.
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

    Task DeleteAsync(
        Guid providerId,
        Guid closureId,
        CancellationToken cancellationToken);
}

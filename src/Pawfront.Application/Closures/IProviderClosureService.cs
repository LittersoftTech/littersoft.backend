namespace Pawfront.Application.Closures;

public interface IProviderClosureService
{
    /// <summary>
    /// Attempt to create a closure. If any confirmed booking falls inside the
    /// requested window the call returns <see cref="CreateClosureResult.BookingsExist"/>
    /// and nothing is persisted — the provider is expected to move/cancel those
    /// bookings first and retry.
    /// </summary>
    Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid providerId,
        Guid closureId,
        CancellationToken cancellationToken);
}

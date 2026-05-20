namespace Pawfront.Application.Closures;

/// <summary>
/// Application orchestrator for provider closures. Implements both the full
/// CRUD service (used by the API endpoints) and the narrow read interface
/// (used by the slot + booking services) — same pattern as BookingService.
/// </summary>
internal sealed class ProviderClosureService(IProviderClosureSqlStore store)
    : IProviderClosureService, IProviderClosureReader
{
    public Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken)
    {
        if (command.EndDate < command.StartDate)
        {
            throw new ArgumentException("EndDate must be on or after StartDate.", nameof(command));
        }

        var hasStart = command.StartTime is not null;
        var hasEnd = command.EndTime is not null;
        if (hasStart != hasEnd)
        {
            throw new ArgumentException(
                "StartTime and EndTime must be provided together, or both omitted.",
                nameof(command));
        }

        if (hasStart && command.StartTime >= command.EndTime)
        {
            throw new ArgumentException(
                "StartTime must be earlier than EndTime.",
                nameof(command));
        }

        // Partial-day window only makes sense for a single calendar day.
        if (hasStart && command.StartDate != command.EndDate)
        {
            throw new ArgumentException(
                "Partial-day closures (StartTime/EndTime) must use the same StartDate and EndDate.",
                nameof(command));
        }

        if (command.Reason is { Length: > 500 })
        {
            throw new ArgumentException("Reason must be 500 characters or fewer.", nameof(command));
        }

        return store.CreateAsync(command, cancellationToken);
    }

    public Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (from is not null && to is not null && to < from)
        {
            throw new ArgumentException("`to` must be on or after `from`.");
        }
        return store.ListAsync(providerId, from, to, cancellationToken);
    }

    public Task DeleteAsync(Guid providerId, Guid closureId, CancellationToken cancellationToken)
        => store.DeleteAsync(providerId, closureId, cancellationToken);

    public Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken)
        => store.GetActiveClosuresForDateAsync(providerId, date, cancellationToken);
}

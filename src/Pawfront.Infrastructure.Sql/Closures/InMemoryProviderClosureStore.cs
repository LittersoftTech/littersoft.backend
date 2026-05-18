using System.Collections.Concurrent;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;

namespace Pawfront.Infrastructure.Sql.Closures;

/// <summary>
/// Dev-fallback in-memory implementation of <see cref="IProviderClosureSqlStore"/>.
/// Mirrors the SQL sproc semantics: conflict check against confirmed bookings, then
/// insert. Race-safety via a per-provider semaphore in lieu of UPDLOCK + HOLDLOCK.
/// </summary>
internal sealed class InMemoryProviderClosureStore(IBookingSqlStore bookingStore)
    : IProviderClosureSqlStore
{
    private readonly ConcurrentDictionary<Guid, ClosureRow> closures = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> providerLocks = new();

    public async Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken)
    {
        var providerLock = providerLocks.GetOrAdd(command.ProviderId, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            // No provider-existence check here — the in-memory bookings table doesn't track
            // providers either. The SQL implementation does enforce this via FK + the 51070 throw.

            // Pull all bookings for this provider, then filter by date range + (optional) time overlap.
            var providerBookings = await bookingStore.ListByProviderAsync(command.ProviderId, cancellationToken);

            var conflicts = providerBookings
                .Where(b => string.Equals(b.Status, "Confirmed", StringComparison.Ordinal))
                .Where(b => b.BookingDate >= command.StartDate && b.BookingDate <= command.EndDate)
                .Where(b =>
                    command.StartTime is null
                    || (b.StartTime < command.EndTime!.Value && b.EndTime > command.StartTime.Value))
                .OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime)
                .Select(b => new ConflictingBooking(b.BookingId, b.PetParentId, b.BookingDate, b.StartTime, b.EndTime))
                .ToArray();

            if (conflicts.Length > 0)
            {
                return new CreateClosureResult.BookingsExist(conflicts);
            }

            var row = new ClosureRow
            {
                ClosureId = Guid.NewGuid(),
                ProviderId = command.ProviderId,
                StartDate = command.StartDate,
                EndDate = command.EndDate,
                StartTime = command.StartTime,
                EndTime = command.EndTime,
                Reason = command.Reason,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            closures[row.ClosureId] = row;
            return new CreateClosureResult.Created(ToClosure(row));
        }
        finally
        {
            providerLock.Release();
        }
    }

    public Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderClosure> list = closures.Values
            .Where(c => c.ProviderId == providerId)
            .Where(c => to   is null || c.StartDate <= to.Value)
            .Where(c => from is null || c.EndDate   >= from.Value)
            .OrderBy(c => c.StartDate).ThenBy(c => c.StartTime ?? TimeOnly.MinValue)
            .Select(ToClosure)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task DeleteAsync(Guid providerId, Guid closureId, CancellationToken cancellationToken)
    {
        if (!closures.TryGetValue(closureId, out var row) || row.ProviderId != providerId)
        {
            throw new ProviderClosureNotFoundException(closureId);
        }
        closures.TryRemove(closureId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveClosure> list = closures.Values
            .Where(c => c.ProviderId == providerId)
            .Where(c => date >= c.StartDate && date <= c.EndDate)
            .OrderBy(c => c.StartTime ?? TimeOnly.MinValue)
            .Select(c => new ActiveClosure(c.ClosureId, c.StartTime, c.EndTime, c.Reason))
            .ToArray();
        return Task.FromResult(list);
    }

    private static ProviderClosure ToClosure(ClosureRow row) => new(
        row.ClosureId, row.ProviderId, row.StartDate, row.EndDate,
        row.StartTime, row.EndTime, row.Reason, row.CreatedAtUtc);

    private sealed class ClosureRow
    {
        public Guid ClosureId { get; init; }
        public Guid ProviderId { get; init; }
        public DateOnly StartDate { get; init; }
        public DateOnly EndDate { get; init; }
        public TimeOnly? StartTime { get; init; }
        public TimeOnly? EndTime { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}

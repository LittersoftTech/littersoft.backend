using System.Collections.Concurrent;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.ProviderServices;

namespace Pawfront.Infrastructure.Sql.Closures;

/// <summary>
/// Dev-fallback in-memory implementation of <see cref="IProviderClosureSqlStore"/>.
/// Mirrors the SQL sproc semantics: validate every ServiceId belongs to the provider
/// and is active, then conflict-check against confirmed bookings on those services,
/// then insert N rows (all-or-nothing). Race-safety via a per-provider semaphore in
/// lieu of UPDLOCK + HOLDLOCK.
/// </summary>
internal sealed class InMemoryProviderClosureStore(
    IBookingSqlStore bookingStore,
    IProviderServiceCatalog catalog) : IProviderClosureSqlStore
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
            // Validate every ServiceId belongs to the provider and is active.
            var providerServices = await catalog.ListByProviderAsync(
                command.ProviderId, includeInactive: true, cancellationToken);
            var ownedActiveServiceIds = providerServices
                .Where(s => s.IsActive)
                .Select(s => s.ServiceId)
                .ToHashSet();
            if (!command.ServiceIds.All(ownedActiveServiceIds.Contains))
            {
                throw new ProviderClosureServiceInvalidException(command.ProviderId);
            }

            var targetServiceIds = command.ServiceIds.ToHashSet();

            var providerBookings = await bookingStore.ListByProviderAsync(command.ProviderId, date: null, cancellationToken);
            var conflicts = providerBookings
                .Where(b => string.Equals(b.Status, "Confirmed", StringComparison.Ordinal))
                .Where(b => targetServiceIds.Contains(b.ServiceId))
                .Where(b => b.BookingDate >= command.StartDate && b.BookingDate <= command.EndDate)
                .Where(b =>
                    command.StartTime is null
                    || (b.StartTime < command.EndTime!.Value && b.EndTime > command.StartTime.Value))
                .OrderBy(b => b.ServiceId).ThenBy(b => b.BookingDate).ThenBy(b => b.StartTime)
                .Select(b => new ConflictingBooking(
                    b.ServiceId, b.BookingId, b.PetParentId, b.Source, b.CustomerName,
                    b.BookingDate, b.StartTime, b.EndTime))
                .ToArray();

            if (conflicts.Length > 0)
            {
                return new CreateClosureResult.BookingsExist(conflicts);
            }

            var now = DateTimeOffset.UtcNow;
            var created = new List<ProviderClosure>();
            foreach (var serviceId in command.ServiceIds)
            {
                var row = new ClosureRow
                {
                    ClosureId = Guid.NewGuid(),
                    ProviderId = command.ProviderId,
                    ServiceId = serviceId,
                    StartDate = command.StartDate,
                    EndDate = command.EndDate,
                    StartTime = command.StartTime,
                    EndTime = command.EndTime,
                    Reason = command.Reason,
                    CreatedAtUtc = now
                };
                closures[row.ClosureId] = row;
                created.Add(ToClosure(row));
            }

            return new CreateClosureResult.Created(created);
        }
        finally
        {
            providerLock.Release();
        }
    }

    public Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        Guid? serviceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderClosure> list = closures.Values
            .Where(c => c.ProviderId == providerId)
            .Where(c => serviceId is null || c.ServiceId == serviceId.Value)
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
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveClosure> list = closures.Values
            .Where(c => c.ServiceId == serviceId)
            .Where(c => date >= c.StartDate && date <= c.EndDate)
            .OrderBy(c => c.StartTime ?? TimeOnly.MinValue)
            .Select(c => new ActiveClosure(c.ClosureId, c.StartTime, c.EndTime, c.Reason))
            .ToArray();
        return Task.FromResult(list);
    }

    private static ProviderClosure ToClosure(ClosureRow row) => new(
        row.ClosureId, row.ProviderId, row.ServiceId, row.StartDate, row.EndDate,
        row.StartTime, row.EndTime, row.Reason, row.CreatedAtUtc);

    private sealed class ClosureRow
    {
        public Guid ClosureId { get; init; }
        public Guid ProviderId { get; init; }
        public Guid ServiceId { get; init; }
        public DateOnly StartDate { get; init; }
        public DateOnly EndDate { get; init; }
        public TimeOnly? StartTime { get; init; }
        public TimeOnly? EndTime { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}

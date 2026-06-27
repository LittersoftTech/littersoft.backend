using System.Collections.Concurrent;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory implementation of <see cref="INightStayBookingSqlStore"/> for the dev
/// fallback path (no SQL conn string + Key Vault disabled). Mirrors the SQL sproc
/// behaviour for the happy path plus the typed exceptions; the per-night capacity
/// check is race-safe via a per-service async lock instead of UPDLOCK + HOLDLOCK.
/// </summary>
internal sealed class InMemoryNightStayBookingStore : INightStayBookingSqlStore
{
    private readonly ConcurrentDictionary<Guid, Row> bookings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> serviceLocks = new();
    private readonly ConcurrentDictionary<Guid, List<BookingStatusHistoryEntry>> history = new();

    private static bool IsActive(Row row) => !BookingStatuses.Cancelled.Contains(row.Status);

    public async Task<NightStayBookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        TimeOnly dropOffTime,
        TimeOnly pickUpTime,
        int capacity,
        CancellationToken cancellationToken)
    {
        var serviceLock = serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            // Reject if any stayed night already has @capacity active overlapping stays.
            for (var night = checkInDate; night < checkOutDate; night = night.AddDays(1))
            {
                var occupied = bookings.Values.Count(b =>
                    b.ServiceId == serviceId
                    && IsActive(b)
                    && b.CheckInDate <= night
                    && b.CheckOutDate > night);

                if (occupied >= capacity)
                {
                    throw new NightStayCapacityExceededException(serviceId, checkInDate, checkOutDate);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var row = new Row
            {
                NightStayBookingId = Guid.NewGuid(),
                ProviderId = providerId,
                PetParentId = petParentId,
                PetId = petId,
                ServiceId = serviceId,
                ServiceCategory = serviceCategory,
                SubCategory = subCategory,
                CheckInDate = checkInDate,
                CheckOutDate = checkOutDate,
                DropOffTime = dropOffTime,
                PickUpTime = pickUpTime,
                Status = BookingStatuses.Created,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CancelledAtUtc = null
            };

            bookings[row.NightStayBookingId] = row;
            AppendHistory(row.NightStayBookingId, null, row.Status, "System", null, "Night stay booking created");
            return ToResult(row);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bookings.TryGetValue(bookingId, out var row);
        return Task.FromResult(row is null ? null : (NightStayBookingResult?)ToResult(row));
    }

    public Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }

        if (row.PetParentId != petParentId)
        {
            throw new NightStayBookingCancellationForbiddenException(bookingId);
        }

        if (BookingStatuses.Cancelled.Contains(row.Status))
        {
            throw new NightStayBookingAlreadyCancelledException(bookingId);
        }

        var now = DateTimeOffset.UtcNow;
        var from = row.Status;
        row.Status = BookingStatuses.ParentCancelled;
        row.CancelledAtUtc = now;
        row.UpdatedAtUtc = now;
        AppendHistory(bookingId, from, row.Status, "Parent", petParentId, null);

        return Task.FromResult(ToResult(row));
    }

    public Task<NightStayBookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }

        var ownsBooking = actor == BookingStatusActor.Provider
            ? row.ProviderId == actorId
            : row.PetParentId == actorId;
        if (!ownsBooking)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }

        var allowed = actor == BookingStatusActor.Provider
            ? BookingStatuses.ProviderSettable
            : BookingStatuses.ParentSettable;
        if (!allowed.Contains(newStatus))
        {
            throw new BookingStatusNotAllowedException(newStatus, actor);
        }

        if (BookingStatuses.Terminal.Contains(row.Status))
        {
            throw new BookingStatusTerminalException(bookingId, row.Status);
        }

        if (row.Status == newStatus)
        {
            throw new BookingStatusUnchangedException(bookingId, newStatus);
        }

        var now = DateTimeOffset.UtcNow;
        var from = row.Status;
        row.Status = newStatus;
        row.UpdatedAtUtc = now;
        if (BookingStatuses.Cancelled.Contains(newStatus))
        {
            row.CancelledAtUtc = now;
        }
        AppendHistory(bookingId, from, newStatus, actor.ToString(), actorId, note);

        return Task.FromResult(ToResult(row));
    }

    public Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NightStayBookingResult> list = bookings.Values
            .Where(b => b.ProviderId == providerId)
            .Where(b => onDate is null || (onDate.Value >= b.CheckInDate && onDate.Value < b.CheckOutDate))
            .OrderByDescending(b => b.CheckInDate)
            .ThenByDescending(b => b.CheckOutDate)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<NightStayBookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NightStayBookingResult> list = bookings.Values
            .Where(b => b.PetParentId == petParentId)
            .OrderByDescending(b => b.CheckInDate)
            .ThenByDescending(b => b.CheckOutDate)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        if (!history.TryGetValue(bookingId, out var list))
        {
            return Task.FromResult<IReadOnlyList<BookingStatusHistoryEntry>>(Array.Empty<BookingStatusHistoryEntry>());
        }

        lock (list)
        {
            IReadOnlyList<BookingStatusHistoryEntry> snapshot = list
                .OrderBy(e => e.ChangedAtUtc)
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    private void AppendHistory(
        Guid bookingId,
        string? fromStatus,
        string toStatus,
        string actor,
        Guid? actorId,
        string? note)
    {
        var list = history.GetOrAdd(bookingId, _ => new List<BookingStatusHistoryEntry>());
        lock (list)
        {
            list.Add(new BookingStatusHistoryEntry(
                Guid.NewGuid(), bookingId, fromStatus, toStatus, actor, actorId, note, DateTimeOffset.UtcNow));
        }
    }

    private static NightStayBookingResult ToResult(Row row) =>
        new(row.NightStayBookingId,
            row.ProviderId,
            row.PetParentId,
            row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.CheckInDate,
            row.CheckOutDate,
            row.DropOffTime,
            row.PickUpTime,
            row.Status,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.CancelledAtUtc,
            row.PetId);

    private sealed class Row
    {
        public Guid NightStayBookingId { get; init; }
        public Guid ProviderId { get; init; }
        public Guid PetParentId { get; init; }
        public Guid? PetId { get; init; }
        public Guid ServiceId { get; init; }
        public required string ServiceCategory { get; init; }
        public required string SubCategory { get; init; }
        public DateOnly CheckInDate { get; init; }
        public DateOnly CheckOutDate { get; init; }
        public TimeOnly DropOffTime { get; init; }
        public TimeOnly PickUpTime { get; init; }
        public required string Status { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
    }
}

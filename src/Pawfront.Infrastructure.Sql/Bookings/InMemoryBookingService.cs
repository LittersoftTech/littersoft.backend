using System.Collections.Concurrent;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory implementation of <see cref="IBookingSqlStore"/> used in the dev fallback path
/// (no SQL conn string + Key Vault disabled). Behaviour mirrors the SQL stored procedures
/// for the happy path plus the typed exceptions; capacity check is race-safe via a per-provider
/// async lock instead of UPDLOCK+HOLDLOCK.
/// </summary>
internal sealed class InMemoryBookingStore : IBookingSqlStore
{
    private readonly ConcurrentDictionary<Guid, BookingRow> bookings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> providerLocks = new();

    public async Task<BookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        string serviceCategory,
        string subCategory,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        int capacity,
        CancellationToken cancellationToken)
    {
        var providerLock = providerLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            // Count overlapping confirmed bookings for this provider+date.
            var concurrent = bookings.Values.Count(b =>
                b.ProviderId == providerId
                && b.BookingDate == bookingDate
                && b.Status == "Confirmed"
                && b.StartTime < endTime
                && b.EndTime > startTime);

            if (concurrent >= capacity)
            {
                throw new BookingCapacityExceededException(providerId, bookingDate, startTime, endTime);
            }

            var now = DateTimeOffset.UtcNow;
            var row = new BookingRow
            {
                BookingId = Guid.NewGuid(),
                ProviderId = providerId,
                PetParentId = petParentId,
                ServiceCategory = serviceCategory,
                SubCategory = subCategory,
                BookingDate = bookingDate,
                StartTime = startTime,
                EndTime = endTime,
                Status = "Confirmed",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CancelledAtUtc = null
            };

            bookings[row.BookingId] = row;
            return ToResult(row);
        }
        finally
        {
            providerLock.Release();
        }
    }

    public Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bookings.TryGetValue(bookingId, out var row);
        return Task.FromResult(row is null ? null : (BookingResult?)ToResult(row));
    }

    public Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new BookingNotFoundException(bookingId);
        }

        if (row.PetParentId != petParentId)
        {
            throw new BookingCancellationForbiddenException(bookingId);
        }

        if (row.Status == "Cancelled")
        {
            throw new BookingAlreadyCancelledException(bookingId);
        }

        var now = DateTimeOffset.UtcNow;
        row.Status = "Cancelled";
        row.CancelledAtUtc = now;
        row.UpdatedAtUtc = now;

        return Task.FromResult(ToResult(row));
    }

    public Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BookingResult> list = bookings.Values
            .Where(b => b.ProviderId == providerId)
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BookingResult> list = bookings.Values
            .Where(b => b.PetParentId == petParentId)
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid providerId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BookingWindow> list = bookings.Values
            .Where(b =>
                b.ProviderId == providerId
                && b.BookingDate == bookingDate
                && b.Status == "Confirmed")
            .OrderBy(b => b.StartTime)
            .Select(b => new BookingWindow(b.StartTime, b.EndTime))
            .ToArray();
        return Task.FromResult(list);
    }

    private static BookingResult ToResult(BookingRow row) =>
        new(row.BookingId,
            row.ProviderId,
            row.PetParentId,
            row.ServiceCategory,
            row.SubCategory,
            row.BookingDate,
            row.StartTime,
            row.EndTime,
            row.Status,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.CancelledAtUtc);

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }
        public Guid ProviderId { get; init; }
        public Guid PetParentId { get; init; }
        public required string ServiceCategory { get; init; }
        public required string SubCategory { get; init; }
        public DateOnly BookingDate { get; init; }
        public TimeOnly StartTime { get; init; }
        public TimeOnly EndTime { get; init; }
        public required string Status { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
    }
}

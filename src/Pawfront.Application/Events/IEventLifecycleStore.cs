namespace Pawfront.Application.Events;

public sealed record EventReliability(bool IsRedFlagged, DateTimeOffset? RedFlagExpiresAt,
    int ConsecutivePaidEventsAttended, int ActiveBookingCount, int ConsecutiveNegativeMarks);

public interface IEventLifecycleStore
{
    Task CancelAsync(Guid eventId, Guid parentId, CancellationToken ct);
    Task RemoveAsync(Guid eventId, Guid parentId, CancellationToken ct);
    Task SetTicketPaymentAsync(Guid bookingId, int ticketNumber, Guid parentId, string paymentStatus, CancellationToken ct);
    Task<EventReliability> GetReliabilityAsync(Guid accountId, CancellationToken ct);
    Task SetWaitlistAsync(Guid eventId, Guid parentId, bool join, int? capacity, CancellationToken ct);
    Task<IReadOnlySet<Guid>> GetWaitlistAsync(Guid parentId, CancellationToken ct);
}

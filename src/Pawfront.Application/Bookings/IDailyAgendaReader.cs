namespace Pawfront.Application.Bookings;

/// <summary>
/// Narrow read interface consumed by the daily-agenda service. The richer
/// sibling of <see cref="IDailyBookingReader"/>: same occupied windows, scoped
/// the same way (by ServiceId, night stays folded in as full-day windows), but
/// each row carries its booking id, job number, owning pet parent, and
/// lifecycle status so the agenda can label the block and mask the rows that
/// belong to somebody else.
/// </summary>
public interface IDailyAgendaReader
{
    Task<IReadOnlyList<AgendaBookingRow>> GetAgendaForDateAsync(
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken);
}

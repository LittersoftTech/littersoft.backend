namespace Pawfront.Application.Badges;

public sealed record BadgeSnapshot(IReadOnlySet<Guid> Parents, IReadOnlySet<Guid> Pets, IReadOnlySet<Guid> EventBookings);
public interface IBadgeReader
{
    Task<BadgeSnapshot> ReadAsync(IReadOnlyCollection<Guid> parentIds, IReadOnlyCollection<Guid> petIds,
        IReadOnlyCollection<Guid> eventBookingIds, CancellationToken cancellationToken);
}

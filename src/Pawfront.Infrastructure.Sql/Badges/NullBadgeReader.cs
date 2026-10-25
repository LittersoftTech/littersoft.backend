using Pawfront.Application.Badges;
namespace Pawfront.Infrastructure.Sql.Badges;
internal sealed class NullBadgeReader : IBadgeReader
{
    public Task<BadgeSnapshot> ReadAsync(IReadOnlyCollection<Guid> parentIds, IReadOnlyCollection<Guid> petIds,
        IReadOnlyCollection<Guid> eventBookingIds, CancellationToken cancellationToken) =>
        Task.FromResult(new BadgeSnapshot(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
}

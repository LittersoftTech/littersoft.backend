namespace Pawfront.Contracts.Badges;

// Explicit opt-in: only display contracts implement these; never requests or identity documents.
public interface IParentBadgeTarget { Guid? BadgeParentId { get; } bool HasIdBadge { get; set; } }
public interface IPetBadgeTarget { Guid? BadgePetId { get; } bool HasMicrochipBadge { get; set; } }
public interface IEventBookerBadgeTarget { Guid? BadgeEventBookingId { get; } bool HasIdBadge { get; set; } }

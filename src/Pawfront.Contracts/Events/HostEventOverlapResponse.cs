namespace Pawfront.Contracts.Events;

/// <summary>Direct HTTP 409 body returned when a host's event intervals overlap.</summary>
public sealed record HostEventOverlapResponse(
    string Code,
    Guid ConflictingEventId,
    string ConflictingEventTitle);

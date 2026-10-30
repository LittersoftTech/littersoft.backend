namespace Pawfront.Application.Events;

public sealed class HostEventOverlapException(Guid conflictingEventId, string conflictingEventTitle)
    : Exception($"This event overlaps '{conflictingEventTitle}'.")
{
    public Guid ConflictingEventId { get; } = conflictingEventId;
    public string ConflictingEventTitle { get; } = conflictingEventTitle;
}

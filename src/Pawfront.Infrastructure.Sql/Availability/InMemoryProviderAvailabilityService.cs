using System.Collections.Concurrent;
using Pawfront.Application.Availability;

namespace Pawfront.Infrastructure.Sql.Availability;

internal sealed class InMemoryProviderAvailabilityService : IProviderAvailabilityService
{
    private readonly ConcurrentDictionary<Guid, List<DayAvailabilityResult>> store = new();

    public Task<ProviderWeeklyAvailabilityResult> SaveAsync(
        SaveProviderWeeklyAvailabilityCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCommand(command);

        var days = command.Days
            .OrderBy(d => d.DayOfWeek)
            .Select(d => new DayAvailabilityResult(
                d.DayOfWeek, d.IsOpen, d.StartTime, d.EndTime, d.BreakStartTime, d.BreakEndTime))
            .ToList();

        store[command.ProviderId] = days;

        return Task.FromResult(new ProviderWeeklyAvailabilityResult(command.ProviderId, days));
    }

    public Task<ProviderWeeklyAvailabilityResult> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        var days = store.TryGetValue(providerId, out var existing)
            ? existing
            : new List<DayAvailabilityResult>();

        return Task.FromResult(new ProviderWeeklyAvailabilityResult(providerId, days));
    }

    private static void ValidateCommand(SaveProviderWeeklyAvailabilityCommand command)
    {
        if (command.Days is null || command.Days.Count != 7)
        {
            throw new ArgumentException(
                "Days must contain exactly 7 entries (one per weekday).",
                nameof(command.Days));
        }

        var seen = new HashSet<int>();
        foreach (var day in command.Days)
        {
            if (day.DayOfWeek is < 0 or > 6)
            {
                throw new ArgumentException(
                    $"DayOfWeek must be between 0 (Sunday) and 6 (Saturday); got {day.DayOfWeek}.",
                    nameof(command.Days));
            }
            if (!seen.Add(day.DayOfWeek))
            {
                throw new ArgumentException(
                    $"DayOfWeek '{day.DayOfWeek}' appears more than once.",
                    nameof(command.Days));
            }

            if (!day.IsOpen)
            {
                if (day.StartTime is not null || day.EndTime is not null
                    || day.BreakStartTime is not null || day.BreakEndTime is not null)
                {
                    throw new ArgumentException(
                        $"Closed day {day.DayOfWeek} must not include time fields.",
                        nameof(command.Days));
                }
                continue;
            }

            if (day.StartTime is null || day.EndTime is null)
            {
                throw new ArgumentException(
                    $"Open day {day.DayOfWeek} requires both StartTime and EndTime.",
                    nameof(command.Days));
            }
            if (day.StartTime >= day.EndTime)
            {
                throw new ArgumentException(
                    $"Day {day.DayOfWeek}: StartTime must be earlier than EndTime.",
                    nameof(command.Days));
            }

            var hasBreakStart = day.BreakStartTime is not null;
            var hasBreakEnd = day.BreakEndTime is not null;
            if (hasBreakStart != hasBreakEnd)
            {
                throw new ArgumentException(
                    $"Day {day.DayOfWeek}: BreakStartTime and BreakEndTime must be set together (or both null).",
                    nameof(command.Days));
            }
            if (hasBreakStart)
            {
                if (day.BreakStartTime >= day.BreakEndTime)
                {
                    throw new ArgumentException(
                        $"Day {day.DayOfWeek}: BreakStartTime must be earlier than BreakEndTime.",
                        nameof(command.Days));
                }
                if (day.BreakStartTime < day.StartTime || day.BreakEndTime > day.EndTime)
                {
                    throw new ArgumentException(
                        $"Day {day.DayOfWeek}: Break must be inside the working window [{day.StartTime}, {day.EndTime}].",
                        nameof(command.Days));
                }
            }
        }
    }
}

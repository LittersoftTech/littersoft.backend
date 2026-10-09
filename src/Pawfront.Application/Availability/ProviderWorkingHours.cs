namespace Pawfront.Application.Availability;

/// <summary>Projects local weekly hours onto the existing UTC booking/slot calendar.</summary>
public static class ProviderWorkingHours
{
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("TimeZone must be a valid timezone, for example Asia/Kolkata or Europe/Zurich.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException($"Unknown TimeZone '{id}'.", nameof(id), ex); }
    }

    public static List<(TimeOnly Start, TimeOnly End)> Windows(
        ProviderWeeklyAvailabilityResult weekly, DateOnly utcDate, bool includeBreak = false)
    {
        var zone = ResolveTimeZone(weekly.TimeZone);
        var from = utcDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var until = from.AddDays(1);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(from, zone));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(until.AddTicks(-1), zone));
        var result = new List<(TimeOnly Start, TimeOnly End)>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var day = weekly.Days.FirstOrDefault(d => d.DayOfWeek == (int)date.DayOfWeek);
            if (day is not { IsOpen: true, StartTime: not null, EndTime: not null }) continue;
            if (!includeBreak && day is { BreakStartTime: not null, BreakEndTime: not null })
            {
                Add(day.StartTime.Value, day.BreakStartTime.Value);
                Add(day.BreakEndTime.Value, day.EndTime.Value);
            }
            else Add(day.StartTime.Value, day.EndTime.Value);

            void Add(TimeOnly start, TimeOnly end)
            {
                if (start >= end) return;
                var a = ToUtc(date.ToDateTime(start), zone);
                var b = ToUtc(date.ToDateTime(end), zone);
                a = a < from ? from : a;
                b = b > until ? until : b;
                if (a >= b) return;
                // The existing UTC APIs represent same-day windows with TimeOnly.
                result.Add((TimeOnly.FromDateTime(a), b == until ? TimeOnly.MaxValue : TimeOnly.FromDateTime(b)));
            }
        }
        return result.OrderBy(w => w.Start).ToList();
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // Match SQL AT TIME ZONE: shift missing wall-clock times by the DST gap;
        // during fall-back use the first occurrence of an ambiguous boundary.
        if (zone.IsInvalidTime(local))
        {
            var before = local;
            var after = local;
            while (zone.IsInvalidTime(before)) before = before.AddMinutes(-1);
            while (zone.IsInvalidTime(after)) after = after.AddMinutes(1);
            local += zone.GetUtcOffset(after) - zone.GetUtcOffset(before);
        }
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

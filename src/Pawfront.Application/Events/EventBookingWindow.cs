namespace Pawfront.Application.Events;

public static class EventBookingWindow
{
    private static readonly TimeZoneInfo EventTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    public static DateTimeOffset StartAtUtc(DateOnly startDate, TimeOnly startTime)
    {
        var localStart = startDate.ToDateTime(startTime, DateTimeKind.Unspecified);
        // Match SQL AT TIME ZONE: move gap times forward by the DST change,
        // and use the pre-change (larger) offset for repeated autumn times.
        if (EventTimeZone.IsInvalidTime(localStart))
            localStart = localStart.Add(EventTimeZone.GetUtcOffset(localStart.AddDays(1))
                - EventTimeZone.GetUtcOffset(localStart.AddDays(-1)));
        var offset = EventTimeZone.IsAmbiguousTime(localStart)
            ? EventTimeZone.GetAmbiguousTimeOffsets(localStart).Max()
            : EventTimeZone.GetUtcOffset(localStart);
        return new DateTimeOffset(localStart, offset).ToUniversalTime();
    }

    public static DateTimeOffset CloseAtUtc(DateOnly startDate, TimeOnly startTime)
        => StartAtUtc(startDate, startTime).AddHours(-1);

    public static bool IsClosed(DateOnly startDate, TimeOnly startTime, DateTimeOffset now)
        => now >= CloseAtUtc(startDate, startTime);
}

public sealed class EventBookingsClosedException()
    : Exception("Ticket bookings for this event are closed.");

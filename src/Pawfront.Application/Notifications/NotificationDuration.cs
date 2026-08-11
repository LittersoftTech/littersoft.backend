using System.Globalization;

namespace Pawfront.Application.Notifications;

/// <summary>
/// Turns a span of time into the words a notification uses for it — "24 hours",
/// "2 hours 15 minutes", "45 minutes".
///
/// Kept apart from <see cref="NotificationLocalTime"/> on purpose: that class
/// exists because a wall-clock reading depends on the recipient's zone, whereas a
/// LENGTH of time is the same everywhere. Nothing here needs a
/// <see cref="TimeZoneInfo"/>, and folding it in would suggest otherwise.
/// </summary>
public static class NotificationDuration
{
    /// <summary>
    /// The span as copy, or null when there is nothing sensible to say — a
    /// deadline that has already passed, or one that rounds away to nothing. The
    /// caller omits the parameter in that case and the renderer's fallback covers
    /// it, which reads better than "Respond within 0 minutes".
    /// </summary>
    /// <remarks>
    /// Rounded to the NEAREST minute rather than truncated. The span is usually
    /// derived from a creation timestamp carrying sub-second precision, so a
    /// window that is conceptually 2h15m arrives as 2h14m59.7s; truncating would
    /// publish "2 hours 14 minutes" for it.
    /// </remarks>
    public static string? Humanise(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return null;
        }

        var totalMinutes = (long)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        if (totalMinutes <= 0)
        {
            return null;
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours == 0)
        {
            return Plural(minutes, "minute");
        }

        return minutes == 0
            ? Plural(hours, "hour")
            : $"{Plural(hours, "hour")} {Plural(minutes, "minute")}";
    }

    private static string Plural(long count, string unit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{count} {unit}{(count == 1 ? string.Empty : "s")}");
}

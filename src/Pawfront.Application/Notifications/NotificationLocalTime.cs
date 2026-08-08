using System.Globalization;

namespace Pawfront.Application.Notifications;

/// <summary>
/// Turns the UTC instants carried on a notification into the wall-clock strings
/// its recipient reads — the ONE place a notification's timezone and its date /
/// time formats are decided.
///
/// <b>Why it exists.</b> Everything in this codebase is stored and computed in
/// UTC (see the UTC convention in CLAUDE.md), and until 2026-08-06 notifications
/// were also *rendered* in UTC: a booking at 14:00 UTC was announced as "14:00"
/// to a provider whose clock said 16:00. Every consumer of both apps is in
/// Switzerland, so a UTC body is simply the wrong time by one or two hours,
/// depending on the season — which is exactly the kind of error a reminder must
/// not make.
///
/// <b>Why here and not in T-SQL.</b> Booking notifications are enqueued from
/// three processes, one of which (the Pawfront.Functions sweeps) is pure T-SQL,
/// so the producers hand over raw instants and the dispatcher formats them at
/// render time. That is the same division of labour <c>serviceName</c> already
/// uses — see the note on it in <c>Notification.EnqueueBookingNotification</c>.
/// Converting in SQL instead would have put the timezone, the DST rules and the
/// format strings in two places that must never disagree.
///
/// <b>PROVISION for per-user timezones.</b> Everyone is Swiss today, so
/// <see cref="Resolve"/> is called with a null id and every recipient gets
/// <see cref="Default"/>. When a <c>TimeZoneId</c> column lands on
/// <c>Provider.Providers</c> / <c>Parent.PetParents</c>, the only change needed is
/// to read it into <c>ClaimedNotification</c> and pass it to
/// <see cref="Resolve"/> — the formatting, the fallback and every call site
/// downstream already take the zone as an argument. An unrecognised or blank id
/// falls back to <see cref="Default"/> rather than throwing: a notification is
/// never worth failing over a bad profile value.
/// </summary>
public static class NotificationLocalTime
{
    /// <summary>
    /// Switzerland — the zone every consumer of both apps is currently in, and so
    /// the fallback for any recipient with no timezone of their own.
    /// </summary>
    public const string DefaultTimeZoneId = "Europe/Zurich";

    /// <summary>
    /// The Windows name for the same zone. .NET converts between the IANA and
    /// Windows time-zone databases on the fly, but only when ICU is available;
    /// naming both means an ICU-less host still resolves Switzerland instead of
    /// silently falling back to UTC.
    /// </summary>
    private const string DefaultWindowsTimeZoneId = "W. Europe Standard Time";

    // "5 Aug" / "14:00" / "5 Aug 14:00". Invariant culture so the wording is
    // deterministic regardless of the host's locale — these reach users as copy.
    private const string DateFormat = "d MMM";
    private const string TimeFormat = @"HH\:mm";
    private const string DateAndTimeFormat = @"d MMM HH\:mm";

    /// <summary>
    /// The zone used for every recipient until per-user timezones exist. Resolved
    /// once at startup; UTC as an absolute last resort, so a host with no
    /// time-zone data degrades to the old behaviour rather than crashing the
    /// dispatcher.
    /// </summary>
    public static TimeZoneInfo Default { get; } =
        FindFirst(DefaultTimeZoneId, DefaultWindowsTimeZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The zone a notification should be rendered in for its recipient.
    /// </summary>
    /// <param name="recipientTimeZoneId">
    /// The recipient's own timezone, once their profile carries one. Null today —
    /// see the PROVISION note on this class.
    /// </param>
    public static TimeZoneInfo Resolve(string? recipientTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(recipientTimeZoneId))
        {
            return Default;
        }

        return FindFirst(recipientTimeZoneId.Trim()) ?? Default;
    }

    /// <summary>"5 Aug" — the local calendar date the instant falls on.</summary>
    public static string FormatDate(DateTimeOffset instantUtc, TimeZoneInfo timeZone) =>
        ToLocal(instantUtc, timeZone).ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>"14:00" — the local clock time.</summary>
    public static string FormatTime(DateTimeOffset instantUtc, TimeZoneInfo timeZone) =>
        ToLocal(instantUtc, timeZone).ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// "5 Aug 14:00" — for a deadline, which can fall on a different day from the
    /// thing it applies to and therefore has to carry its date.
    /// </summary>
    public static string FormatDateAndTime(DateTimeOffset instantUtc, TimeZoneInfo timeZone) =>
        ToLocal(instantUtc, timeZone).ToString(DateAndTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an instant out of a notification's data parameters.
    ///
    /// Accepts both shapes the producers emit: T-SQL's
    /// <c>CONVERT(..., 126)</c> ("2026-08-05T14:00:00", no offset) and .NET's
    /// round-trip "O" ("2026-08-05T14:00:00.0000000+00:00"). A value with no
    /// offset is taken as UTC, which is what every producer means by one.
    ///
    /// Returns false rather than throwing on anything unparseable, so one bad row
    /// costs its own placeholder — the renderer's fallback covers it — instead of
    /// the whole dispatch batch.
    /// </summary>
    public static bool TryParse(string? value, out DateTimeOffset instantUtc)
    {
        instantUtc = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        instantUtc = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    /// <summary>
    /// Writes an instant in the form <see cref="TryParse"/> reads back — used by
    /// the C# producers so they emit exactly what the T-SQL ones do.
    /// </summary>
    public static string ToIso(DateTimeOffset instantUtc) =>
        instantUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime ToLocal(DateTimeOffset instantUtc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(instantUtc, timeZone).DateTime;

    private static TimeZoneInfo? FindFirst(params string[] timeZoneIds)
    {
        foreach (var id in timeZoneIds)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone))
            {
                return timeZone;
            }
        }

        return null;
    }
}

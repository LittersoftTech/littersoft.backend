namespace Pawfront.Application.Earnings;

/// <summary>
/// The period an earnings / spend figure is scoped to. Periods are
/// <b>calendar-aligned</b>, not rolling windows — "Monthly" means the current
/// calendar month, not the last 30 days, because that is what a provider
/// reconciles against their own records.
/// </summary>
public enum EarningsPeriod
{
    /// <summary>The current calendar week, Monday through Sunday.</summary>
    Weekly,

    /// <summary>The current calendar month.</summary>
    Monthly,

    /// <summary>The current calendar quarter (Jan–Mar, Apr–Jun, Jul–Sep, Oct–Dec).</summary>
    Quarterly,

    /// <summary>The current calendar year.</summary>
    Yearly,

    /// <summary>Everything on record — the unbounded "up till now" overview.</summary>
    AllTime
}

/// <summary>
/// Resolves an <see cref="EarningsPeriod"/> into the inclusive service-date range
/// the SQL layer filters on.
/// </summary>
/// <remarks>
/// <para>
/// Boundaries are computed in <b>UTC</b>, matching the codebase-wide convention
/// (every stored date and time here is UTC). A provider in a +01:00/+02:00 zone
/// therefore sees a month roll over at 01:00/02:00 local rather than midnight.
/// That is a deliberate consistency choice, not an oversight: introducing a
/// per-request offset here would make earnings the only surface in the system
/// whose date boundaries disagree with bookings, closures and availability.
/// Revisit it together with the provider-timezone column that push notifications
/// also want.
/// </para>
/// <para>
/// The range covers the <b>whole</b> calendar period, including days still in the
/// future, rather than stopping at today. A booking can legitimately be marked
/// COMPLETED ahead of its service date (a freelance vet appointment, say), and
/// truncating at today would drop it from the very period it belongs to.
/// </para>
/// </remarks>
public static class EarningsPeriodRange
{
    /// <summary>
    /// The inclusive <c>[From, To]</c> service-date range for <paramref name="period"/>
    /// relative to <paramref name="today"/>. <see cref="EarningsPeriod.AllTime"/>
    /// resolves to <c>(null, null)</c> — no bound in either direction.
    /// </summary>
    public static (DateOnly? From, DateOnly? To) Resolve(EarningsPeriod period, DateOnly today) => period switch
    {
        EarningsPeriod.Weekly => ResolveWeek(today),
        EarningsPeriod.Monthly => ResolveMonth(today),
        EarningsPeriod.Quarterly => ResolveQuarter(today),
        EarningsPeriod.Yearly => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
        EarningsPeriod.AllTime => (null, null),
        _ => (null, null)
    };

    /// <summary>
    /// Parses the wire value of a <c>period</c> query parameter, case-insensitively.
    /// A missing or blank value defaults to <see cref="EarningsPeriod.AllTime"/> —
    /// the unbounded overview is the safe default for a screen that hasn't chosen a
    /// filter yet. Throws <see cref="ArgumentException"/> for an unknown value so the
    /// endpoint can map it to a 400.
    /// </summary>
    public static EarningsPeriod Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EarningsPeriod.AllTime;
        }

        return Enum.TryParse<EarningsPeriod>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported period '{value}'. Expected one of: " +
                $"{string.Join(", ", Enum.GetNames<EarningsPeriod>())}.",
                nameof(value));
    }

    private static (DateOnly?, DateOnly?) ResolveWeek(DateOnly today)
    {
        // Monday-start week. DayOfWeek is Sunday = 0, so shift by 6 to make Monday
        // the zero point rather than special-casing Sunday.
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var start = today.AddDays(-daysSinceMonday);
        return (start, start.AddDays(6));
    }

    private static (DateOnly?, DateOnly?) ResolveMonth(DateOnly today)
    {
        var start = new DateOnly(today.Year, today.Month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private static (DateOnly?, DateOnly?) ResolveQuarter(DateOnly today)
    {
        var firstMonthOfQuarter = ((today.Month - 1) / 3 * 3) + 1;
        var start = new DateOnly(today.Year, firstMonthOfQuarter, 1);
        return (start, start.AddMonths(3).AddDays(-1));
    }
}

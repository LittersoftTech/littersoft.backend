namespace Pawfront.Application.Earnings;

/// <summary>
/// Expands the friendly status groups the apps filter by into the raw lifecycle
/// statuses stored on a booking row.
/// </summary>
/// <remarks>
/// <para>
/// The grouping lives here, in one place, rather than in T-SQL: the sprocs take a
/// plain comma-separated list of raw statuses, so adding a lifecycle state means
/// editing <see cref="Pawfront.Application.Bookings.BookingStatuses"/> and this
/// file — not four stored procedures. Raw status names are also accepted, so a
/// client that wants to filter on exactly <c>IN_PROGRESS</c> can.
/// </para>
/// <para>
/// Shared by the pet-parent booking history and the provider earnings breakdown so
/// the two hosts accept exactly the same vocabulary — a client that learns
/// <c>status=Cancelled</c> on one API does not discover the other spells it
/// differently. (It was <c>ParentBookingStatusFilter</c> while only the parent side
/// used it.)
/// </para>
/// <para>
/// <see cref="CancelledGroup"/> is the broad "it didn't happen" bucket and is the
/// union of <see cref="NoShowGroup"/>, <see cref="ExpiredGroup"/> and the three
/// genuinely-cancelled statuses. The two narrow groups exist because a payouts
/// screen needs to separate them: being stood up is a different story from a parent
/// cancelling, and the provider earnings summary reports those slices as separate
/// figures.
/// </para>
/// </remarks>
public static class BookingStatusFilter
{
    /// <summary>The job happened and money moved.</summary>
    public const string CompletedGroup = "Completed";

    /// <summary>Still live — booked, confirmed, underway, or mid-modification.</summary>
    public const string UpcomingGroup = "Upcoming";

    /// <summary>Didn't happen: cancelled, declined, no-showed or expired.</summary>
    public const string CancelledGroup = "Cancelled";

    /// <summary>A party failed to appear. A narrow slice of <see cref="CancelledGroup"/>.</summary>
    public const string NoShowGroup = "NoShow";

    /// <summary>
    /// Ran out of time rather than being called off: never accepted, the job window
    /// passed, or the start code was got wrong six times. A narrow slice of
    /// <see cref="CancelledGroup"/>.
    /// </summary>
    public const string ExpiredGroup = "Expired";

    private static readonly IReadOnlySet<string> CompletedStatuses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Bookings.BookingStatuses.Completed,
            Bookings.BookingStatuses.Paid
        };

    private static readonly IReadOnlySet<string> NoShowStatuses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Bookings.BookingStatuses.ParentNoShow,
            Bookings.BookingStatuses.ProviderNoShow
        };

    private static readonly IReadOnlySet<string> ExpiredStatuses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Bookings.BookingStatuses.Expired,
            Bookings.BookingStatuses.JobExpired,
            Bookings.BookingStatuses.OtpMaxAttemptsExceeded
        };

    private static readonly IReadOnlySet<string> UpcomingStatuses =
        Bookings.BookingStatuses.All
            .Where(s => !CompletedStatuses.Contains(s) && !Bookings.BookingStatuses.Cancelled.Contains(s))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Turns the caller's <c>status</c> values — group names, raw statuses, or a mix
    /// — into a distinct list of raw statuses. An empty input means "no filter" and
    /// returns an empty list. Throws <see cref="ArgumentException"/> on an
    /// unrecognised value so the endpoint can answer 400 rather than silently
    /// returning everything.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (string.Equals(value, CompletedGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(CompletedStatuses);
            }
            else if (string.Equals(value, UpcomingGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(UpcomingStatuses);
            }
            else if (string.Equals(value, CancelledGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(Bookings.BookingStatuses.Cancelled);
            }
            else if (string.Equals(value, NoShowGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(NoShowStatuses);
            }
            else if (string.Equals(value, ExpiredGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(ExpiredStatuses);
            }
            else if (Bookings.BookingStatuses.All.Contains(value))
            {
                expanded.Add(value);
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported status filter '{value}'. Expected one of the groups " +
                    $"{CompletedGroup} / {UpcomingGroup} / {CancelledGroup} / {NoShowGroup} / " +
                    $"{ExpiredGroup}, or a raw booking status.",
                    nameof(values));
            }
        }

        return expanded.ToArray();
    }
}

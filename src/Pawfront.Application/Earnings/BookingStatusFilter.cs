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
/// <para>
/// <see cref="UpcomingGroup"/> is split the same way, for the provider's job list:
/// <see cref="PendingAcceptanceGroup"/>, <see cref="AcceptedGroup"/>,
/// <see cref="InProgressGroup"/> and <see cref="ModificationRequestGroup"/> are
/// DISJOINT and together cover it exactly. That in turn makes the six groups
/// PendingAcceptance / Accepted / InProgress / ModificationRequest / Completed /
/// Cancelled a partition of every status a booking can hold — which is what lets
/// the job screen offer them as a set of checkboxes and be sure no job falls
/// through all of them.
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

    /// <summary>
    /// Waiting on the provider to accept or decline. A narrow slice of
    /// <see cref="UpcomingGroup"/>.
    /// </summary>
    public const string PendingAcceptanceGroup = "PendingAcceptance";

    /// <summary>
    /// Accepted and not yet started — the confirmed-equivalent resting states.
    /// A narrow slice of <see cref="UpcomingGroup"/>.
    /// </summary>
    public const string AcceptedGroup = "Accepted";

    /// <summary>
    /// The provider has begun: they tapped Start (START_JOB, waiting on the
    /// parent's code) or the job is genuinely underway (IN_PROGRESS). A narrow
    /// slice of <see cref="UpcomingGroup"/>.
    /// </summary>
    public const string InProgressGroup = "InProgress";

    /// <summary>
    /// A schedule change is staged and awaiting the counterparty's answer. A
    /// narrow slice of <see cref="UpcomingGroup"/>.
    /// </summary>
    public const string ModificationRequestGroup = "ModificationRequest";

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
    /// CREATED, plus the deprecated APPROVAL_NEEDED. The legacy status is folded
    /// in here rather than left out of every narrow group: nothing produces it any
    /// more, but an old row carrying it is still a booking nobody has answered,
    /// and a row that belonged to no group would vanish from a screen that filters
    /// by all four.
    /// </summary>
    private static readonly IReadOnlySet<string> PendingAcceptanceStatuses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Bookings.BookingStatuses.Created,
            Bookings.BookingStatuses.ApprovalNeeded
        };

    /// <summary>
    /// The five confirmed-equivalent resting states — CONFIRMED and the four a
    /// resolved modification leaves behind. Reuses
    /// <see cref="Bookings.BookingStatuses.ConfirmedEquivalent"/> rather than
    /// restating it, so "which states can a job be started from" and "which states
    /// does the app call Accepted" cannot drift apart.
    /// </summary>
    private static readonly IReadOnlySet<string> AcceptedStatuses =
        Bookings.BookingStatuses.ConfirmedEquivalent;

    /// <summary>
    /// START_JOB and IN_PROGRESS, plus the two retired intermediates kept for
    /// legacy rows. START_JOB belongs here rather than under
    /// <see cref="AcceptedStatuses"/> because the provider has already tapped
    /// Start and is being shown the enter-the-code screen — from their agenda the
    /// job has begun, whatever the parent has yet to hand over.
    /// </summary>
    private static readonly IReadOnlySet<string> InProgressStatuses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Bookings.BookingStatuses.StartJob,
            Bookings.BookingStatuses.InProgress,
            Bookings.BookingStatuses.Ending,
            Bookings.BookingStatuses.JobStarted
        };

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
            else if (string.Equals(value, PendingAcceptanceGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(PendingAcceptanceStatuses);
            }
            else if (string.Equals(value, AcceptedGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(AcceptedStatuses);
            }
            else if (string.Equals(value, InProgressGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(InProgressStatuses);
            }
            else if (string.Equals(value, ModificationRequestGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.UnionWith(Bookings.BookingStatuses.ModificationRequested);
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
                    $"{ExpiredGroup} / {PendingAcceptanceGroup} / {AcceptedGroup} / " +
                    $"{InProgressGroup} / {ModificationRequestGroup}, or a raw booking status.",
                    nameof(values));
            }
        }

        return expanded.ToArray();
    }
}

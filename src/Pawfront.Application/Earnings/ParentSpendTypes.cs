using Pawfront.Application.Bookings;

namespace Pawfront.Application.Earnings;

/// <summary>
/// A pet parent's booking counts and spend for one filtered set.
/// </summary>
/// <remarks>
/// <para>
/// <c>CompletedBookings</c> / <c>UpcomingBookings</c> / <c>CancelledBookings</c>
/// are mutually exclusive and sum to <c>TotalBookings</c> — the three states a
/// parent actually thinks in: it happened, it's going to, or it fell through.
/// Declines, no-shows and expiries all land in "cancelled", because from the
/// parent's side they mean the same thing.
/// </para>
/// <para>
/// <c>AmountSpent</c> counts bookings that reached COMPLETED or PAID. A job the
/// provider has finished but not yet tapped "mark paid" on is included: the parent
/// handed over the cash and has no control over that tap, so excluding it would
/// show them a total they know to be wrong. <c>AwaitingPaymentBookings</c> reports
/// how many of those there are. <c>UpcomingAmount</c> is money not yet spent —
/// what confirmed future bookings will cost — and is deliberately kept out of
/// <c>AmountSpent</c>.
/// </para>
/// </remarks>
public sealed record ParentBookingSummary(
    int TotalBookings,
    int SingleDayBookings,
    int NightStayBookings,
    int CompletedBookings,
    int CancelledBookings,
    int UpcomingBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    decimal AmountSpent,
    decimal UpcomingAmount)
{
    /// <summary>An all-zero summary, for a parent with nothing matching the filters.</summary>
    public static ParentBookingSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0m, 0m);
}

/// <summary>The parent's summary together with the period range it covers.</summary>
public sealed record ParentBookingSummaryResult(
    EarningsPeriod Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ParentBookingSummary Summary);

/// <summary>
/// One booking on the parent's history feed. Single-day and night-stay bookings
/// are merged into one list — a parent thinks "my bookings", not "my two kinds of
/// bookings" — so <c>BookingType</c> discriminates and the fields belonging to the
/// other kind are null. <c>ProviderName</c> is the provider's personal name from
/// SQL (their business name lives in Cosmos and isn't joinable here, exactly as on
/// the booking-detail read). The Pawfront commission is deliberately not on this
/// shape: the parent pays the gross amount either way, and the split is the
/// provider's concern.
/// </summary>
public sealed record ParentBookingHistoryRow(
    string BookingType,
    Guid BookingId,
    string JobId,
    string Status,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly ServiceDate,
    DateOnly? BookingDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    DateOnly? CheckInDate,
    DateOnly? CheckOutDate,
    int? Nights,
    Guid ProviderId,
    string? ProviderName,
    Guid? PetId,
    string? PetName,
    string? PetProfilePhotoUrl,
    bool IsCompleted,
    bool IsPaid,
    decimal? Amount,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod);

/// <summary>Sort key for the parent's booking history.</summary>
public enum ParentHistorySortBy
{
    /// <summary>By service date.</summary>
    Date,

    /// <summary>By the booking's amount.</summary>
    Amount
}

/// <summary>Filter + paging arguments shared by the parent summary and history endpoints.</summary>
public sealed record ParentBookingHistoryQuery(
    Guid PetParentId,
    EarningsPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    Guid? PetId,
    IReadOnlyList<string> Statuses,
    ParentHistorySortBy SortBy,
    EarningsSortDirection SortDirection,
    int Skip,
    int Take);

/// <summary>
/// Expands the friendly status groups the parent app filters by into the raw
/// lifecycle statuses stored on a booking row.
/// </summary>
/// <remarks>
/// The grouping lives here, in one place, rather than in T-SQL: the sprocs take a
/// plain comma-separated list of raw statuses, so adding a lifecycle state means
/// editing <see cref="BookingStatuses"/> and this file — not four stored
/// procedures. Raw status names are also accepted, so a client that wants to
/// filter on exactly <c>IN_PROGRESS</c> can.
/// </remarks>
public static class ParentBookingStatusFilter
{
    /// <summary>The job happened and money moved.</summary>
    public const string CompletedGroup = "Completed";

    /// <summary>Still live — booked, confirmed, underway, or mid-modification.</summary>
    public const string UpcomingGroup = "Upcoming";

    /// <summary>Didn't happen: cancelled, declined, no-showed or expired.</summary>
    public const string CancelledGroup = "Cancelled";

    private static readonly IReadOnlySet<string> CompletedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { BookingStatuses.Completed, BookingStatuses.Paid };

    private static readonly IReadOnlySet<string> UpcomingStatuses =
        BookingStatuses.All
            .Where(s => !CompletedStatuses.Contains(s) && !BookingStatuses.Cancelled.Contains(s))
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
                expanded.UnionWith(BookingStatuses.Cancelled);
            }
            else if (BookingStatuses.All.Contains(value))
            {
                expanded.Add(value);
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported status filter '{value}'. Expected one of the groups " +
                    $"{CompletedGroup} / {UpcomingGroup} / {CancelledGroup}, or a raw booking status.",
                    nameof(values));
            }
        }

        return expanded.ToArray();
    }
}

namespace Pawfront.Application.Earnings;

/// <summary>
/// One page of results plus the unpaged total, so a client can render "showing
/// 20 of 137" and know whether to fetch more.
/// </summary>
/// <remarks>
/// <c>PeriodStart</c> / <c>PeriodEnd</c> are the inclusive service-date bounds the
/// page was actually built from — explicit dates override the requested period, so
/// they are reported back rather than left for the caller to re-derive. Resolving
/// them here also means they are computed from the same instant as the query; a
/// second computation in the endpoint could straddle midnight UTC and echo a
/// different range than was searched.
/// </remarks>
public sealed record PagedEarningsResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Skip,
    int Take,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd)
{
    /// <summary>True when rows remain beyond this page.</summary>
    public bool HasMore => Skip + Items.Count < TotalCount;
}

/// <summary>
/// A provider's money for one date range.
/// </summary>
/// <remarks>
/// <para>
/// Three money views, because with cash they answer different questions:
/// <list type="bullet">
/// <item><b>Gross</b> — what the parent pays. The provider physically holds this.</item>
/// <item><b>Fee</b> — Pawfront's commission on it. With cash the provider has
/// collected it on Pawfront's behalf and owes it back.</item>
/// <item><b>Net</b> — Gross minus Fee: what the provider actually keeps. This is
/// the headline figure.</item>
/// </list>
/// Each is further split into <c>Received</c> (marked PAID) and <c>Awaiting</c>
/// (job COMPLETED, payment not yet recorded), which together make up the totals.
/// </para>
/// <para>
/// <c>UnpricedBookings</c> counts completed bookings with no resolvable price —
/// legacy rows created before price-lock whose offering has since been removed.
/// It is reported so a total that is missing money is visibly incomplete rather
/// than quietly low.
/// </para>
/// <para>
/// <c>PrivateJob*</c> covers Custom walk-ins the provider recorded themselves.
/// Those are arranged off-platform, so Pawfront takes no commission and they can
/// never be marked PAID — they are therefore kept OUT of every other figure here
/// and reported separately, so the provider sees the work without it distorting
/// platform earnings.
/// </para>
/// <para>
/// <c>CancelledJob*</c> / <c>NoShowJob*</c> / <c>ExpiredJob*</c> are the UNREALISED
/// jobs: booked, then nothing. They are what a payouts screen needs to explain the
/// gap between what was on the calendar and what was earned — a thin month reads
/// very differently when three jobs were no-shows. They are deliberately NOT part
/// of <see cref="GrossAmount"/> or <see cref="NetAmount"/>: no money moved, so
/// folding them in would misstate what the provider holds. Each amount is what the
/// job would have been worth, from its creation-time price-lock; no fee is
/// reported, because a commission on money that never changed hands is not owed.
/// The three buckets are disjoint, and their sum is <see cref="UnrealisedAmount"/>
/// — the money behind a <c>status=Cancelled</c> query on the bookings list.
/// </para>
/// <para>
/// Bookings still in flight are in neither set: they have not happened and have not
/// failed, so counting them anywhere here would be a forecast rather than a figure.
/// </para>
/// </remarks>
public sealed record ProviderEarningsTotals(
    int CompletedBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    decimal GrossAmount,
    decimal PawfrontFee,
    decimal ReceivedGross,
    decimal ReceivedFee,
    decimal AwaitingGross,
    decimal AwaitingFee,
    int PrivateJobCount,
    decimal PrivateJobAmount,
    int CancelledJobCount,
    decimal CancelledJobAmount,
    int NoShowJobCount,
    decimal NoShowJobAmount,
    int ExpiredJobCount,
    decimal ExpiredJobAmount)
{
    /// <summary>What the provider keeps: <see cref="GrossAmount"/> less <see cref="PawfrontFee"/>.</summary>
    public decimal NetAmount => GrossAmount - PawfrontFee;

    /// <summary>Net on bookings already marked paid.</summary>
    public decimal ReceivedNet => ReceivedGross - ReceivedFee;

    /// <summary>Net still owed to the provider on completed-but-unpaid bookings.</summary>
    public decimal AwaitingNet => AwaitingGross - AwaitingFee;

    /// <summary>
    /// Every unrealised job in range, counted once: cancelled + no-show + expired.
    /// The three buckets are disjoint, so this is a plain sum.
    /// </summary>
    public int UnrealisedJobCount => CancelledJobCount + NoShowJobCount + ExpiredJobCount;

    /// <summary>
    /// What the unrealised jobs would have been worth. Reported so the provider can
    /// see the money that did not arrive; never added to <see cref="GrossAmount"/>.
    /// </summary>
    public decimal UnrealisedAmount => CancelledJobAmount + NoShowJobAmount + ExpiredJobAmount;

    /// <summary>An all-zero total, for a provider with nothing in range.</summary>
    public static ProviderEarningsTotals Empty { get; } =
        new(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
}

/// <summary>Totals for one named period, with the resolved range they cover.</summary>
public sealed record ProviderEarningsPeriodResult(
    EarningsPeriod Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderEarningsTotals Totals);

/// <summary>
/// The provider earnings landing screen in one round trip: lifetime totals plus
/// the three periods a provider checks most often.
/// </summary>
public sealed record ProviderEarningsOverview(
    ProviderEarningsTotals AllTime,
    ProviderEarningsPeriodResult ThisWeek,
    ProviderEarningsPeriodResult ThisMonth,
    ProviderEarningsPeriodResult ThisYear);

/// <summary>
/// One booking on the provider's earnings breakdown. Single-day and night-stay
/// bookings share this shape; <c>BookingType</c> discriminates, and the fields
/// that only apply to one kind are null on the other. <c>JobId</c> is the friendly
/// <c>PF-000123</c> label; <c>PayoutId</c> the <c>PO-000123</c> reference minted
/// when the job completed (null on a private job). <c>ServiceDate</c> is the date
/// the earning is attributed to — the booking date, or the checkout date of a stay.
/// <c>IsEarned</c> says whether this row produced money (COMPLETED / PAID) or is one
/// of the unrealised ones a status filter can pull in; it is emitted rather than
/// re-derived from <c>Status</c> in the app, so the rule lives in one place.
/// </summary>
public sealed record ProviderEarningsBookingRow(
    string BookingType,
    Guid BookingId,
    string JobId,
    string? PayoutId,
    string PayoutStatus,
    string Status,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly ServiceDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    DateOnly? CheckInDate,
    DateOnly? CheckOutDate,
    int? Nights,
    string? CustomerName,
    string? PetName,
    bool IsPaid,
    bool IsPrivate,
    bool IsEarned,
    decimal? GrossAmount,
    decimal? PawfrontFee,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod)
{
    /// <summary>What the provider keeps on this booking; null when it can't be priced.</summary>
    public decimal? NetAmount => GrossAmount is null ? null : GrossAmount - (PawfrontFee ?? 0m);
}

/// <summary>Sort key for the provider earnings breakdown.</summary>
public enum EarningsSortBy
{
    /// <summary>By service date.</summary>
    Date,

    /// <summary>By the booking's gross amount.</summary>
    Earnings
}

/// <summary>Sort direction shared by the earnings and history listings.</summary>
public enum EarningsSortDirection
{
    Ascending,
    Descending
}

/// <summary>Filter + paging arguments for the provider earnings breakdown.</summary>
/// <remarks>
/// <c>Statuses</c> is the expanded raw-status list from
/// <see cref="BookingStatusFilter.Expand"/>; empty means "no status filter", which
/// the store reads as the earned rows only — the behaviour every caller had before
/// the filter existed.
/// </remarks>
public sealed record ProviderEarningsBookingQuery(
    Guid ProviderId,
    EarningsPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    IReadOnlyList<string> Statuses,
    EarningsSortBy SortBy,
    EarningsSortDirection SortDirection,
    int Skip,
    int Take);

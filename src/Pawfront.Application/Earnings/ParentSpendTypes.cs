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

namespace Pawfront.Contracts.Earnings;

/// <summary>
/// A provider's money for one date range.
/// </summary>
/// <remarks>
/// Three views of the same bookings, because with cash they answer different
/// questions: <c>grossAmount</c> is what parents pay (the provider physically
/// holds it), <c>pawfrontFee</c> is the platform commission they have collected on
/// Pawfront's behalf and owe back, and <c>netAmount</c> is what they keep — the
/// headline figure. Each splits into <c>received*</c> (marked paid) and
/// <c>awaiting*</c> (job completed, payment not yet recorded).
/// <para>
/// <c>unpricedBookings</c> counts completed bookings whose price could not be
/// resolved (legacy rows predating price-lock whose offering is gone); it is
/// reported so a total missing money is visibly incomplete.
/// </para>
/// <para>
/// <c>privateJob*</c> covers Custom walk-ins. Those are off-platform, carry no
/// commission and can never be marked paid, so they are excluded from every other
/// figure here and reported separately.
/// </para>
/// <para>
/// <c>cancelledJob*</c> / <c>noShowJob*</c> / <c>expiredJob*</c> are the UNREALISED
/// jobs — booked, then nothing — and <c>unrealisedAmount</c> is their sum. They
/// account for the gap between what was on the calendar and what was earned, and
/// are deliberately NOT part of <c>grossAmount</c> / <c>netAmount</c>: no money
/// moved, so adding them would misstate what the provider holds. Each amount is
/// what the job would have been worth; no fee is reported against them, because a
/// commission on money that never changed hands is not owed. The three buckets are
/// disjoint and map to the bookings list's <c>Cancelled</c> (all three),
/// <c>NoShow</c> and <c>Expired</c> status groups. Bookings still in flight are in
/// neither set — they have not happened and have not failed.
/// </para>
/// </remarks>
public sealed record ProviderEarningsTotalsResponse(
    int CompletedBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    decimal GrossAmount,
    decimal PawfrontFee,
    decimal NetAmount,
    decimal ReceivedGross,
    decimal ReceivedFee,
    decimal ReceivedNet,
    decimal AwaitingGross,
    decimal AwaitingFee,
    decimal AwaitingNet,
    int PrivateJobCount,
    decimal PrivateJobAmount,
    int CancelledJobCount,
    decimal CancelledJobAmount,
    int NoShowJobCount,
    decimal NoShowJobAmount,
    int ExpiredJobCount,
    decimal ExpiredJobAmount,
    int UnrealisedJobCount,
    decimal UnrealisedAmount);

/// <summary>
/// Totals for one named period. <c>periodStart</c> / <c>periodEnd</c> are the
/// resolved inclusive service-date bounds (both null for <c>AllTime</c>), echoed
/// back so the client can label the screen without recomputing calendar maths.
/// </summary>
public sealed record ProviderEarningsPeriodResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderEarningsTotalsResponse Totals);

/// <summary>
/// The earnings landing screen: lifetime totals plus the three periods a provider
/// checks most often, all computed from one instant.
/// </summary>
public sealed record ProviderEarningsOverviewResponse(
    ProviderEarningsTotalsResponse AllTime,
    ProviderEarningsPeriodResponse ThisWeek,
    ProviderEarningsPeriodResponse ThisMonth,
    ProviderEarningsPeriodResponse ThisYear);

/// <summary>
/// One booking on the earnings breakdown. <c>bookingType</c> is
/// <c>SingleDay</c> or <c>NightStay</c> and says which of the two shapes is
/// populated: <c>startTime</c>/<c>endTime</c> for a single-day booking,
/// <c>checkInDate</c>/<c>checkOutDate</c>/<c>nights</c> for a stay.
/// <c>serviceDate</c> is the date the earning is attributed to (booking date, or
/// checkout date for a stay) and is what the period filters and sorting use.
/// <c>isPrivate</c> marks an off-platform Custom walk-in: it is listed because it
/// is real work, but it is not part of the summary's platform totals.
/// <c>isEarned</c> says whether the row produced money (COMPLETED / PAID) or is one
/// of the unrealised ones a <c>status</c> filter pulls in — read it rather than
/// re-deriving the rule from <c>status</c>. On an unrealised row <c>grossAmount</c>
/// is what the job WOULD have been worth and <c>pawfrontFee</c> / <c>netAmount</c>
/// are not money anybody owes.
/// </summary>
public sealed record ProviderEarningsBookingResponse(
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
    decimal? NetAmount,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod);

/// <summary>
/// A page of the earnings breakdown, with the filters that produced it echoed back
/// and the unpaged <c>totalCount</c> so the client can page. <c>statuses</c> is the
/// expanded raw-status list the page was actually built from — empty when no
/// <c>status</c> was asked for, which means the earned rows only.
/// </summary>
public sealed record ProviderEarningsBookingsResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    IReadOnlyList<string> Statuses,
    string SortBy,
    string SortDirection,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore,
    IReadOnlyList<ProviderEarningsBookingResponse> Items);

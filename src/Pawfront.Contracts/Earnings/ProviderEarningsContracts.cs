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
    decimal PrivateJobAmount);

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
    decimal? GrossAmount,
    decimal? PawfrontFee,
    decimal? NetAmount,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod);

/// <summary>
/// A page of the earnings breakdown, with the filters that produced it echoed back
/// and the unpaged <c>totalCount</c> so the client can page.
/// </summary>
public sealed record ProviderEarningsBookingsResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    string SortBy,
    string SortDirection,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore,
    IReadOnlyList<ProviderEarningsBookingResponse> Items);

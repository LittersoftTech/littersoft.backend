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
/// <summary>
/// A provider's work and money for one range with private (Custom walk-in) jobs
/// INCLUDED — the two numbers the dashboard's "Bookings" and "Earnings" cards
/// show.
/// </summary>
/// <remarks>
/// <para>
/// Every other figure on these endpoints keeps walk-ins OUT, and is right to:
/// they are off-platform, carry no Pawfront commission and can never be marked
/// paid, so folding them into <c>grossAmount</c> would misstate what the platform
/// processed and what fee is owed. But a provider reading "jobs" and "earnings"
/// means all of their work, so the combined figures are reported here rather than
/// left for the app to add up.
/// </para>
/// <para>
/// <c>acceptedJobs</c> is accepted marketplace bookings + uncancelled walk-ins. It
/// deliberately EXCLUDES requests the provider has not answered yet — those are
/// <c>pendingBookings</c>, and a card that moves the moment a parent taps Book is
/// reporting demand, not workload.
/// </para>
/// <para>
/// There is no received/awaiting split here: a walk-in can never be marked paid,
/// so its money is neither received (no ledger row) nor awaiting (nothing is owed
/// — the provider took the cash at the time). Read that split off the platform
/// figures.
/// </para>
/// <para>
/// <c>grossAmount</c> is what the customer paid; <c>netAmount</c> is that less the
/// platform fee. A CHF 100 day care at 10% commission is 100 and 90 — a tile
/// labelled "earned" almost certainly wants <c>grossAmount</c>.
/// </para>
/// </remarks>
public sealed record ProviderFiguresIncludingPrivateResponse(
    int AcceptedJobs,
    int CompletedJobs,
    decimal GrossAmount,
    decimal NetAmount);

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
    decimal UnrealisedAmount,
    // --- Added 2026-09-11, appended last -------------------------------------
    // Bookings still waiting on the provider to accept or decline (CREATED, plus
    // the deprecated APPROVAL_NEEDED).
    int PendingBookings,
    // Bookings the provider ACCEPTED and which did not fall through —
    // confirmed-equivalent, underway and finished alike. pendingBookings +
    // acceptedBookings + unrealisedJobCount partition the platform side of the
    // range. To list exactly these rows call the bookings list with
    // ?status=Accepted,InProgress,ModificationRequest,Completed.
    int AcceptedBookings,
    // Custom walk-ins the provider recorded and has not cancelled. Unlike
    // privateJobCount (which feeds a money figure and is gated on the job being
    // finished), this counts work taken on.
    int PrivateAcceptedJobs,
    // Platform + private together — what the dashboard cards show. See the type.
    ProviderFiguresIncludingPrivateResponse IncludingPrivate);

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
    string? PaymentMethod,
    // The customer card. This list is the CUSTOMER level of the analytics
    // drill-down (?serviceId= narrows it to the service whose figure was
    // tapped), and previously carried two bare names — so a row showing who
    // booked and which animal needed a second call to the booking detail.
    //
    // Resolved live, so a deleted account reads its anonymised placeholder. All
    // three are null on a Custom walk-in, which has no parent or pet record:
    // its customer is the free text in customerName / petName above.
    string? Breed = null,
    string? PetGender = null,
    string? CustomerPhotoUrl = null);

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

namespace Pawfront.Contracts.Earnings;

/// <summary>
/// A pet parent's booking counts and spend for one filtered set.
/// </summary>
/// <remarks>
/// <c>completedBookings</c> / <c>upcomingBookings</c> / <c>cancelledBookings</c>
/// are mutually exclusive and sum to <c>totalBookings</c>. Declines, no-shows and
/// expiries all count as cancelled — from the parent's side they all mean the
/// booking didn't happen.
/// <para>
/// <c>amountSpent</c> covers bookings that reached COMPLETED or PAID. A job the
/// provider finished but hasn't yet tapped "mark paid" on is included, since the
/// parent already handed over the cash and has no control over that tap;
/// <c>awaitingPaymentBookings</c> says how many those are. <c>upcomingAmount</c>
/// is what confirmed future bookings will cost and is deliberately NOT part of
/// <c>amountSpent</c>.
/// </para>
/// </remarks>
public sealed record ParentBookingSummaryResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    int TotalBookings,
    int SingleDayBookings,
    int NightStayBookings,
    int CompletedBookings,
    int UpcomingBookings,
    int CancelledBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    decimal AmountSpent,
    decimal UpcomingAmount);

/// <summary>
/// One booking on the parent's history feed — single-day and night-stay merged
/// into one list, with <c>bookingType</c> (<c>SingleDay</c> | <c>NightStay</c>)
/// saying which shape is populated. <c>serviceDate</c> is what the date filters
/// and sorting use (booking date, or checkout date for a stay).
/// <c>providerName</c> is the provider's personal name; their business name lives
/// in the service listing, same as on the booking-detail read. The Pawfront
/// commission is deliberately absent — the parent pays <c>amount</c> either way.
/// </summary>
public sealed record ParentBookingHistoryItemResponse(
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

/// <summary>
/// A page of the parent's booking history, with the filters that produced it
/// echoed back and the unpaged <c>totalCount</c> so the client can page.
/// </summary>
public sealed record ParentBookingHistoryResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    string SortBy,
    string SortDirection,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore,
    IReadOnlyList<ParentBookingHistoryItemResponse> Items);

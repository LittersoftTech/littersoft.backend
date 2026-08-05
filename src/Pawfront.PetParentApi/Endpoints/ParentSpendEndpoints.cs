using Pawfront.Application.Earnings;
using Pawfront.Contracts.Earnings;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// The parent's booking overview and paginated history — the mirror of the
/// provider host's earnings endpoints, reading the same underlying amounts.
/// </summary>
/// <remarks>
/// Both routes sit on the ownership-filtered <c>/pet-parents/{petParentId:guid}</c>
/// group, so a caller can only ever see their own bookings; the id is resolved
/// from the JWT and the route is only checked against it.
/// <para>
/// These are additions, not replacements: the existing unpaginated
/// <c>GET /pet-parents/{id}/bookings</c> and <c>/night-stay-bookings</c> keep
/// working unchanged for the screens already built on them.
/// </para>
/// </remarks>
internal static class ParentSpendEndpoints
{
    public static IEndpointRouteBuilder MapParentSpendEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/pet-parents/{petParentId:guid}/bookings").RequireOwnedPetParent();

        // Counts + spend for the same filtered set the history below returns.
        group.MapGet("/summary", GetSummary);
        // Paginated history, single-day and night-stay merged into one feed.
        group.MapGet("/history", GetHistory);

        return builder;
    }

    private static async Task<IResult> GetSummary(
        Guid petParentId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        Guid? petId,
        string? status,
        IParentSpendService spendService,
        CancellationToken cancellationToken)
    {
        ParentBookingHistoryQuery query;
        try
        {
            query = BuildQuery(
                petParentId, period, from, to, petId, status,
                sortBy: null, sortDirection: null, skip: null, take: null);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest("InvalidRequest", "'from' must be on or before 'to'.");
        }

        var result = await spendService.GetSummaryAsync(query, cancellationToken);
        var summary = result.Summary;

        return ApiResults.Ok(new ParentBookingSummaryResponse(
            result.Period.ToString(),
            result.PeriodStart,
            result.PeriodEnd,
            summary.TotalBookings,
            summary.SingleDayBookings,
            summary.NightStayBookings,
            summary.CompletedBookings,
            summary.UpcomingBookings,
            summary.CancelledBookings,
            summary.PaidBookings,
            summary.AwaitingPaymentBookings,
            summary.UnpricedBookings,
            summary.AmountSpent,
            summary.UpcomingAmount));
    }

    private static async Task<IResult> GetHistory(
        Guid petParentId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        Guid? petId,
        string? status,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        IParentSpendService spendService,
        CancellationToken cancellationToken)
    {
        ParentBookingHistoryQuery query;
        try
        {
            query = BuildQuery(petParentId, period, from, to, petId, status, sortBy, sortDirection, skip, take);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest("InvalidRequest", "'from' must be on or before 'to'.");
        }

        var page = await spendService.ListAsync(query, cancellationToken);

        return ApiResults.Ok(new ParentBookingHistoryResponse(
            query.Period.ToString(),
            page.PeriodStart,
            page.PeriodEnd,
            query.SortBy.ToString(),
            query.SortDirection.ToString(),
            page.TotalCount,
            page.Skip,
            page.Take,
            page.HasMore,
            page.Items.Select(ToItem).ToArray()));
    }

    /// <summary>
    /// Builds the shared query from the raw query string. Throws
    /// <see cref="ArgumentException"/> on any unrecognised value so both handlers
    /// answer 400 rather than silently ignoring a filter the caller meant.
    /// </summary>
    private static ParentBookingHistoryQuery BuildQuery(
        Guid petParentId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        Guid? petId,
        string? status,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take)
    {
        // `status` accepts a comma-separated mix of friendly groups
        // (Completed / Upcoming / Cancelled) and raw lifecycle statuses.
        var statuses = ParentBookingStatusFilter.Expand(
            status?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return new ParentBookingHistoryQuery(
            petParentId,
            EarningsPeriodRange.Parse(period),
            from,
            to,
            petId,
            statuses,
            EarningsQueryParsing.ParseParentHistorySortBy(sortBy),
            EarningsQueryParsing.ParseSortDirection(sortDirection),
            skip ?? 0,
            // 0 lets the service apply its own page-size cap rather than
            // duplicating the rule here.
            take ?? 0);
    }

    private static ParentBookingHistoryItemResponse ToItem(ParentBookingHistoryRow row) =>
        new(row.BookingType,
            row.BookingId,
            row.JobId,
            row.Status,
            row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.ServiceItemCode,
            row.ServiceDate,
            row.BookingDate,
            row.StartTime,
            row.EndTime,
            row.CheckInDate,
            row.CheckOutDate,
            row.Nights,
            row.ProviderId,
            row.ProviderName,
            row.PetId,
            row.PetName,
            row.PetProfilePhotoUrl,
            row.IsCompleted,
            row.IsPaid,
            row.Amount,
            row.PaidAtUtc,
            row.PaymentMethod);
}

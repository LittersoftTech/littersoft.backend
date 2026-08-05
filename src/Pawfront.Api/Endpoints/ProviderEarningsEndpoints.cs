using Pawfront.Api.Auth;
using Pawfront.Application.Earnings;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Earnings;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Provider earnings reporting: the overview tiles, the period-filtered totals,
/// and the paginated booking-level breakdown behind them.
/// </summary>
/// <remarks>
/// Unlike most of this host — which trusts the route's <c>providerId</c> — every
/// route here resolves the caller's OWN ProviderId from the JWT and rejects a
/// mismatch with 403. This is revenue data: a provider being able to read a
/// competitor's takings by guessing a GUID is a materially different exposure from
/// the profile-shaped reads elsewhere. Same posture (and same helper shape) as the
/// account-delete endpoint.
/// </remarks>
internal static class ProviderEarningsEndpoints
{
    public static IEndpointRouteBuilder MapProviderEarningsEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/earnings");

        // Landing screen: lifetime totals + this week / month / year in one call.
        group.MapGet("/overview", GetOverview);
        // One period's totals (?period=Weekly|Monthly|Quarterly|Yearly|AllTime).
        group.MapGet("/", GetForPeriod);
        // Which bookings produced the money — paginated, filterable, sortable.
        group.MapGet("/bookings", ListEarningsBookings);

        return builder;
    }

    private static async Task<IResult> GetOverview(
        Guid providerId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderEarningsService earningsService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var overview = await earningsService.GetOverviewAsync(providerId, cancellationToken);

        return ApiResults.Ok(new ProviderEarningsOverviewResponse(
            ToTotals(overview.AllTime),
            ToPeriod(overview.ThisWeek),
            ToPeriod(overview.ThisMonth),
            ToPeriod(overview.ThisYear)));
    }

    private static async Task<IResult> GetForPeriod(
        Guid providerId,
        string? period,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderEarningsService earningsService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        EarningsPeriod parsedPeriod;
        try
        {
            parsedPeriod = EarningsPeriodRange.Parse(period);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        var result = await earningsService.GetForPeriodAsync(providerId, parsedPeriod, cancellationToken);
        return ApiResults.Ok(ToPeriod(result));
    }

    private static async Task<IResult> ListEarningsBookings(
        Guid providerId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderEarningsService earningsService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        EarningsPeriod parsedPeriod;
        EarningsSortBy parsedSortBy;
        EarningsSortDirection parsedDirection;
        try
        {
            parsedPeriod = EarningsPeriodRange.Parse(period);
            parsedSortBy = EarningsQueryParsing.ParseEarningsSortBy(sortBy);
            parsedDirection = EarningsQueryParsing.ParseSortDirection(sortDirection);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest(
                "InvalidRequest", "'from' must be on or before 'to'.");
        }

        // take = 0 lets the service apply its own cap rather than duplicating the
        // page-size rule here.
        var page = await earningsService.ListBookingsAsync(
            new ProviderEarningsBookingQuery(
                providerId, parsedPeriod, from, to, parsedSortBy, parsedDirection,
                skip ?? 0, take ?? 0),
            cancellationToken);

        return ApiResults.Ok(new ProviderEarningsBookingsResponse(
            parsedPeriod.ToString(),
            page.PeriodStart,
            page.PeriodEnd,
            parsedSortBy.ToString(),
            parsedDirection.ToString(),
            page.TotalCount,
            page.Skip,
            page.Take,
            page.HasMore,
            page.Items.Select(ToBooking).ToArray()));
    }

    /// <summary>
    /// Returns a 403 result when the JWT's provider isn't the one in the route, and
    /// null when the caller may proceed. A caller with no provider profile at all
    /// resolves to null and is therefore also refused — they have no earnings to read.
    /// </summary>
    private static async Task<IResult?> EnsureCallerOwnsProviderAsync(
        Guid providerId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        Guid? callerProviderId;
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);
            callerProviderId = caller.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            callerProviderId = null;
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        return callerProviderId == providerId
            ? null
            : ApiResults.Forbidden("Forbidden", "You can only view your own earnings.");
    }

    private static ProviderEarningsPeriodResponse ToPeriod(ProviderEarningsPeriodResult result) =>
        new(result.Period.ToString(), result.PeriodStart, result.PeriodEnd, ToTotals(result.Totals));

    private static ProviderEarningsTotalsResponse ToTotals(ProviderEarningsTotals totals) =>
        new(totals.CompletedBookings,
            totals.PaidBookings,
            totals.AwaitingPaymentBookings,
            totals.UnpricedBookings,
            totals.GrossAmount,
            totals.PawfrontFee,
            totals.NetAmount,
            totals.ReceivedGross,
            totals.ReceivedFee,
            totals.ReceivedNet,
            totals.AwaitingGross,
            totals.AwaitingFee,
            totals.AwaitingNet,
            totals.PrivateJobCount,
            totals.PrivateJobAmount);

    private static ProviderEarningsBookingResponse ToBooking(ProviderEarningsBookingRow row) =>
        new(row.BookingType,
            row.BookingId,
            row.JobId,
            row.PayoutId,
            row.PayoutStatus,
            row.Status,
            row.ServiceCategory,
            row.SubCategory,
            row.ServiceItemCode,
            row.ServiceDate,
            row.StartTime,
            row.EndTime,
            row.CheckInDate,
            row.CheckOutDate,
            row.Nights,
            row.CustomerName,
            row.PetName,
            row.IsPaid,
            row.IsPrivate,
            row.GrossAmount,
            row.PawfrontFee,
            row.NetAmount,
            row.PaidAtUtc,
            row.PaymentMethod);
}

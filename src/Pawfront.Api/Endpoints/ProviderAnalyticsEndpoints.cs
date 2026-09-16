using Pawfront.Api.Auth;
using Pawfront.Application.Analytics;
using Pawfront.Application.Earnings;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Analytics;
using Pawfront.Contracts.Earnings;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's PawPrints analytics: three levels per metric — a dashboard
/// figure, a per-service breakdown, and a customer list.
/// </summary>
/// <remarks>
/// <para>
/// Views are answered here in full. Bookings and earnings get their first two
/// levels here; their third level is the existing
/// <c>GET .../earnings/bookings</c>, which gained a <c>?serviceId=</c> filter and
/// the customer fields the viewer cards carry — a second paginated booking list
/// would have meant two definitions of a provider's jobs.
/// </para>
/// <para>
/// Every route resolves the caller's OWN ProviderId from the JWT and rejects a
/// mismatch with 403, the same posture (and same helper shape) as the earnings,
/// ratings, blocks, invoice and account-delete routes. It matters as much here as
/// for revenue: the viewer list names real pet parents and their pets, so trusting
/// the route's id would hand a competitor's customer list to anyone who could
/// guess a GUID.
/// </para>
/// </remarks>
internal static class ProviderAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapProviderAnalyticsEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/analytics");

        // Levels 1 + 2 of the Views card: the figure and its per-service breakdown.
        group.MapGet("/views", GetViewSummary);
        // Level 3: which parents looked. ?serviceId= narrows to the row they tapped.
        group.MapGet("/views/viewers", ListViewers);
        // Levels 1 + 2 of the Bookings and Earnings cards, in one read.
        group.MapGet("/bookings", GetBookingBreakdown);

        return builder;
    }

    private static async Task<IResult> GetViewSummary(
        Guid providerId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderAnalyticsService analyticsService,
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
            // Same calendar-aligned vocabulary the earnings endpoints take, so a
            // client learns one set of period names for the whole dashboard.
            parsedPeriod = EarningsPeriodRange.Parse(period);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest("InvalidRequest", "'from' must be on or before 'to'.");
        }

        var summary = await analyticsService.GetViewSummaryAsync(
            providerId, parsedPeriod, from, to, cancellationToken);

        return ApiResults.Ok(new ProviderViewSummaryResponse(
            summary.Period.ToString(),
            summary.PeriodStart,
            summary.PeriodEnd,
            new ProviderViewTotalsResponse(
                summary.Totals.TotalViews,
                summary.Totals.UniqueViewers,
                summary.Totals.UnattributedViews,
                summary.Totals.AnonymousViews,
                summary.Totals.LastViewedAtUtc),
            summary.Services.Select(ToServiceView).ToArray()));
    }

    private static async Task<IResult> ListViewers(
        Guid providerId,
        Guid? serviceId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        int? skip,
        int? take,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderAnalyticsService analyticsService,
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

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest("InvalidRequest", "'from' must be on or before 'to'.");
        }

        // take = 0 lets the service apply its own cap rather than duplicating the
        // page-size rule here.
        var page = await analyticsService.ListViewersAsync(
            new ProviderServiceViewerQuery(
                providerId, serviceId, parsedPeriod, from, to, skip ?? 0, take ?? 0),
            cancellationToken);

        return ApiResults.Ok(new ProviderServiceViewersResponse(
            parsedPeriod.ToString(),
            page.PeriodStart,
            page.PeriodEnd,
            serviceId,
            page.TotalCount,
            page.Skip,
            page.Take,
            page.HasMore,
            page.Items.Select(ToViewer).ToArray()));
    }

    private static async Task<IResult> GetBookingBreakdown(
        Guid providerId,
        string? period,
        DateOnly? from,
        DateOnly? to,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderAnalyticsService analyticsService,
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

        if (from is not null && to is not null && from > to)
        {
            return ApiResults.BadRequest("InvalidRequest", "'from' must be on or before 'to'.");
        }

        var breakdown = await analyticsService.GetBookingBreakdownAsync(
            providerId, parsedPeriod, from, to, cancellationToken);

        return ApiResults.Ok(new ProviderBookingBreakdownResponse(
            breakdown.Period.ToString(),
            breakdown.PeriodStart,
            breakdown.PeriodEnd,
            ToFigures(breakdown.Totals),
            breakdown.Services.Select(ToServiceBooking).ToArray()));
    }

    private static ProviderServiceViewResponse ToServiceView(ProviderServiceViewBreakdown row) =>
        new(row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.ServiceType,
            row.IsActive,
            row.Views,
            row.UniqueViewers,
            row.LastViewedAtUtc);

    private static ProviderServiceViewerResponse ToViewer(ProviderServiceViewerRow row) =>
        new(row.PetParentId,
            row.ParentName,
            row.ParentPhotoUrl,
            row.PetId,
            row.PetName,
            row.PetType,
            row.Breed,
            row.PetGender,
            row.ViewCount,
            row.FirstViewedAtUtc,
            row.LastViewedAtUtc);

    private static ProviderServiceBookingResponse ToServiceBooking(ProviderServiceBookingBreakdown row) =>
        new(row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.ServiceType,
            row.IsActive,
            ToFigures(row.Figures));

    /// <summary>
    /// Shared by the totals block and every service row — the same figures at both
    /// levels of the drill-down, so one tile renders either.
    /// </summary>
    internal static ProviderBookingFiguresResponse ToFigures(ProviderBookingFigures figures) =>
        new(figures.TotalBookings,
            figures.CompletedBookings,
            figures.PaidBookings,
            figures.AwaitingPaymentBookings,
            figures.UnpricedBookings,
            figures.UpcomingBookings,
            figures.GrossAmount,
            figures.PawfrontFee,
            figures.NetAmount,
            figures.ReceivedGross,
            figures.ReceivedFee,
            figures.ReceivedNet,
            figures.AwaitingGross,
            figures.AwaitingFee,
            figures.AwaitingNet,
            figures.PrivateJobCount,
            figures.PrivateJobAmount,
            figures.CancelledJobCount,
            figures.CancelledJobAmount,
            figures.NoShowJobCount,
            figures.NoShowJobAmount,
            figures.ExpiredJobCount,
            figures.ExpiredJobAmount,
            figures.UnrealisedJobCount,
            figures.UnrealisedAmount,
            figures.PendingBookings,
            figures.AcceptedBookings,
            figures.PrivateAcceptedJobs,
            ToIncludingPrivate(figures.IncludingPrivate));

    /// <summary>
    /// The combined platform + private block. One mapper, shared by the analytics
    /// figures and the earnings totals, so the two endpoints cannot disagree about
    /// what a provider's own dashboard shows.
    /// </summary>
    internal static ProviderFiguresIncludingPrivateResponse ToIncludingPrivate(
        ProviderFiguresIncludingPrivate figures) =>
        new(figures.AcceptedJobs,
            figures.CompletedJobs,
            figures.GrossAmount,
            figures.NetAmount);

    /// <summary>
    /// Returns a 403 result when the JWT's provider isn't the one in the route, and
    /// null when the caller may proceed. A caller with no provider profile resolves
    /// to null and is therefore also refused — they have no analytics to read.
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
            : ApiResults.Forbidden("Forbidden", "You can only view your own analytics.");
    }
}

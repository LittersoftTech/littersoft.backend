using Pawfront.Api.Auth;
using Pawfront.Application.Bookings;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.Reviews;
using Pawfront.Contracts.Reviews;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's side of reviews: rating the pet parent after a finished booking, and
/// reading the reviews parents have left for them.
/// </summary>
/// <remarks>
/// <para>
/// The provider direction is rating ONLY — 1 to 5, no comment, no photos. That is a
/// product decision, enforced in three places: this host exposes no comment field, the
/// service drops one if a direct caller sends it, and
/// <c>CK_BookingReviews_ProviderRatingHasNoComment</c> refuses to store one.
/// </para>
/// <para>
/// The two SUBMIT routes resolve the caller's own ProviderId from the JWT and reject a
/// mismatch with 403, joining the earnings routes and account-delete as the exceptions
/// to this host's usual "trust the route id" posture. It matters more here than for a
/// read: the sproc's party check compares the actor against the booking's ProviderId, so
/// trusting the route would let anyone who knew a provider id and one of their booking
/// ids write a rating AS that provider.
/// </para>
/// <para>
/// The LIST route is deliberately not ownership-checked — a provider's received reviews
/// are public data, served unfiltered on the parent host too, so restricting it here
/// would only stop the provider seeing what every parent can already see.
/// </para>
/// </remarks>
internal static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapProviderReviewEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}");

        // Provider rates the pet parent on a finished job (rating only).
        group.MapPost("/bookings/{bookingId:guid}/rating", RateParentOnBooking);
        group.MapPost("/night-stay-bookings/{bookingId:guid}/rating", RateParentOnNightStay);

        // The reviews parents have left for this provider, with the summary.
        group.MapGet("/reviews", ListProviderReviews);

        return builder;
    }

    private static Task<IResult> RateParentOnBooking(
        Guid providerId,
        Guid bookingId,
        SubmitParentRatingRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => RateAsync(
            ReviewedBookingTypes.SingleDay, providerId, bookingId, request,
            httpContext, onboardingService, reviewService, cancellationToken);

    private static Task<IResult> RateParentOnNightStay(
        Guid providerId,
        Guid bookingId,
        SubmitParentRatingRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => RateAsync(
            ReviewedBookingTypes.NightStay, providerId, bookingId, request,
            httpContext, onboardingService, reviewService, cancellationToken);

    /// <summary>
    /// Shared submit for both booking kinds. Resubmitting replaces the rating on the
    /// same row, so this answers 200 on a correction and 201 only for a new rating.
    /// </summary>
    private static async Task<IResult> RateAsync(
        string bookingType,
        Guid providerId,
        Guid bookingId,
        SubmitParentRatingRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A rating is required.");
        }

        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var existing = await reviewService.GetAsync(
                bookingType, bookingId, ReviewerTypes.Provider, cancellationToken);

            var review = await reviewService.SubmitAsync(
                new SubmitBookingReviewCommand(
                    bookingType,
                    bookingId,
                    ReviewerTypes.Provider,
                    providerId,
                    request.Rating,
                    // Rating only — see the class remarks.
                    Comment: null),
                cancellationToken);

            var response = ReviewResponseMapping.ToResponse(review);
            var segment = bookingType == ReviewedBookingTypes.NightStay
                ? "night-stay-bookings"
                : "bookings";

            return existing is null
                ? ApiResults.Created(
                    $"/api/v1/providers/{providerId}/{segment}/{bookingId}/rating", response)
                : ApiResults.Ok(response);
        }
        catch (NightStayBookingNotFoundException exception)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", exception.Message);
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
        catch (ReviewForbiddenException exception)
        {
            return ApiResults.Forbidden("Forbidden", exception.Message);
        }
        catch (BookingNotReviewableException exception)
        {
            return ApiResults.Conflict("BookingNotReviewable", exception.Message);
        }
        catch (ReviewNotAppBookingException exception)
        {
            return ApiResults.BadRequest("ReviewNotAppBooking", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListProviderReviews(
        Guid providerId,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        ProviderReviewQuery query;
        try
        {
            query = new ProviderReviewQuery(
                providerId,
                ReviewQueryParsing.ParseSortBy(sortBy),
                ReviewQueryParsing.ParseSortDirection(sortDirection),
                skip ?? 0,
                // 0 lets the service apply its own page-size cap rather than
                // duplicating the rule here.
                take ?? 0);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        var page = await reviewService.ListForProviderAsync(query, cancellationToken);
        return ApiResults.Ok(ReviewResponseMapping.ToProviderReviews(providerId, page));
    }

    /// <summary>
    /// Same shape as the earnings and account-delete checks: resolve the caller's own
    /// ProviderId from the JWT and compare it to the route.
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
            : ApiResults.Forbidden("Forbidden", "You can only rate customers on your own bookings.");
    }
}

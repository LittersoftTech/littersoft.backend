using Pawfront.Application.Reviews;
using Pawfront.Contracts.Reviews;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Application review records to wire contracts. Duplicated on the provider host,
/// matching how the two hosts already each carry their own <c>ApiResults</c> and
/// <c>BlobImageEndpoints</c> — there is no shared host library, and the alternative
/// (mapping logic in <c>Pawfront.Contracts</c>) would break the layering.
/// </summary>
internal static class ReviewResponseMapping
{
    public static BookingReviewResponse ToResponse(BookingReviewRecord review)
        => new(
            review.BookingReviewId,
            review.BookingType,
            review.BookingId,
            review.ReviewerType,
            review.ProviderId,
            review.PetParentId,
            review.Rating,
            review.Comment,
            [.. review.Photos.Select(p => new BookingReviewPhotoResponse(
                p.BookingReviewPhotoId, p.BookingReviewId, p.PhotoUrl, p.CreatedAtUtc))],
            review.CreatedAtUtc,
            review.UpdatedAtUtc);

    public static ProviderReviewsResponse ToProviderReviews(
        Guid providerId,
        ProviderReviewListResult page)
        => new(
            providerId,
            ToSummary(page.Summary),
            [.. page.Items.Select(ToItem)],
            page.Skip,
            page.Take,
            page.HasMore);

    public static ProviderReviewItemResponse ToItem(ProviderReviewRow row)
        => new(
            row.BookingReviewId,
            row.BookingType,
            row.BookingId,
            // Same 'PF-000123' formatting the booking-detail read uses, so one job
            // shows one id across every screen. Null when the job number could not be
            // resolved rather than a misleading "PF-000000".
            row.JobNumber is null ? null : $"PF-{row.JobNumber.Value:D6}",
            row.PetParentId,
            row.ParentName,
            row.ParentPhotoUrl,
            row.Rating,
            row.Comment,
            row.PhotoUrls,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);

    public static ReviewSummaryResponse ToSummary(ReviewSummary summary)
        => new(
            summary.ReviewCount,
            summary.AverageRating,
            summary.FiveStar,
            summary.FourStar,
            summary.ThreeStar,
            summary.TwoStar,
            summary.OneStar);

    /// <summary>
    /// The review block for a booking-detail read. <paramref name="status"/> and
    /// <paramref name="source"/> decide <c>canReview</c>: COMPLETED or PAID, and an App
    /// booking — a Custom walk-in has no pet-parent record, so neither side can review
    /// it. This mirrors the gate in <c>Review.UpsertBookingReview</c>; SQL remains the
    /// authority, this only tells the app whether to offer the prompt.
    /// </summary>
    public static BookingReviewDetailsSection ToDetailsSection(
        string status,
        string source,
        BookingReviewRecord? review)
        => new(
            CanReview: IsReviewable(status, source),
            MyReview: review is null ? null : ToResponse(review));

    /// <summary>
    /// Night-stay overload. Night-stay bookings are App-only (their
    /// <c>PetParentId</c> is NOT NULL), so there is no Custom walk-in case to exclude.
    /// </summary>
    public static BookingReviewDetailsSection ToDetailsSection(
        string status,
        BookingReviewRecord? review)
        => new(
            CanReview: IsReviewable(status, "App"),
            MyReview: review is null ? null : ToResponse(review));

    private static bool IsReviewable(string status, string source)
        => string.Equals(source, "App", StringComparison.OrdinalIgnoreCase)
            && status is "COMPLETED" or "PAID";
}

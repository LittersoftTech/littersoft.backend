namespace Pawfront.Application.Reviews;

/// <summary>
/// Which booking table a review's <c>BookingId</c> points at. Strings rather than an
/// enum to match the rest of the Application layer's booking types (see
/// <c>ProviderEarningsBookingRow.BookingType</c>) and the <c>BookingType</c> column
/// on <c>Booking.BookingPayments</c> / <c>Review.BookingReviews</c>.
/// </summary>
public static class ReviewedBookingTypes
{
    public const string SingleDay = "SingleDay";
    public const string NightStay = "NightStay";
}

/// <summary>
/// Which direction a review runs in. <c>Parent</c> is the pet parent reviewing the
/// provider (rating + optional comment + optional photos); <c>Provider</c> is the
/// provider rating the pet parent (rating only).
/// </summary>
public static class ReviewerTypes
{
    public const string Parent = "Parent";
    public const string Provider = "Provider";
}

/// <summary>
/// The bounds the review feature is built to. Kept together so the endpoint
/// validation, the sproc defaults, and the docs cannot drift apart.
/// </summary>
public static class ReviewLimits
{
    /// <summary>
    /// Matches <c>Review.BookingReviews.Comment NVARCHAR(1000)</c>. Characters, not
    /// words — a word count cannot be expressed as a column constraint, so a
    /// character bound is the one the database can actually enforce alongside C#.
    /// </summary>
    public const int MaxCommentLength = 1000;

    /// <summary>
    /// Per review. Also passed to <c>Review.AddBookingReviewPhoto</c>, which is
    /// where the cap is enforced race-safely — two uploads in flight would each see
    /// room and both insert if C# were the only check.
    /// </summary>
    public const int MaxPhotos = 5;

    /// <summary>
    /// Page cap for the provider-reviews list, applied server-side. Same figure the
    /// earnings and spend lists use, so a client learns one paging rule.
    /// </summary>
    public const int MaxPageSize = 20;

    public const int MinRating = 1;
    public const int MaxRating = 5;
}

/// <summary>Sort key for the provider-reviews list.</summary>
public enum ReviewSortBy
{
    /// <summary>The date the review was given (its <c>CreatedAtUtc</c>).</summary>
    Date = 0,

    /// <summary>The rating score, newest-first within a score band.</summary>
    Rating = 1
}

/// <summary>
/// One party's review of a booking, with its photos. Photos are always empty for a
/// provider-authored row — that direction is rating-only.
/// </summary>
public sealed record BookingReviewRecord(
    Guid BookingReviewId,
    string BookingType,
    Guid BookingId,
    string ReviewerType,
    Guid ProviderId,
    Guid PetParentId,
    int Rating,
    string? Comment,
    IReadOnlyList<BookingReviewPhotoRecord> Photos,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BookingReviewPhotoRecord(
    Guid BookingReviewPhotoId,
    Guid BookingReviewId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A removed review photo. The URL is carried out so the caller can make its
/// best-effort attempt at the blob — the SQL row is the source of truth.
/// </summary>
public sealed record DeletedBookingReviewPhoto(
    Guid BookingReviewPhotoId,
    Guid BookingReviewId,
    string PhotoUrl,
    DateTimeOffset DeletedAtUtc);

/// <summary>
/// Aggregate over every review a provider has received. The histogram is what lets
/// the profile show the usual bar chart without a second call.
/// </summary>
/// <remarks>
/// <see cref="AverageRating"/> is null when <see cref="ReviewCount"/> is 0 — a
/// provider nobody has reviewed has no average, which is a different statement from
/// an average of zero and should render differently ("No reviews yet", not "0.0").
/// </remarks>
public sealed record ReviewSummary(
    int ReviewCount,
    decimal? AverageRating,
    int FiveStar,
    int FourStar,
    int ThreeStar,
    int TwoStar,
    int OneStar)
{
    public static ReviewSummary Empty { get; } = new(0, null, 0, 0, 0, 0, 0);
}

/// <summary>
/// One row of a provider's public review list. The parent's name and photo are
/// resolved live at read time, so a parent who has deleted their account shows as
/// "Deleted User" rather than leaving their real name frozen here.
/// </summary>
/// <param name="JobNumber">
/// Raw sequential job number; the caller formats the <c>PF-000123</c> label exactly
/// as the booking-detail read does, so the two surfaces show the same id.
/// </param>
public sealed record ProviderReviewRow(
    Guid BookingReviewId,
    string BookingType,
    Guid BookingId,
    int? JobNumber,
    Guid PetParentId,
    string? ParentName,
    string? ParentPhotoUrl,
    int Rating,
    string? Comment,
    IReadOnlyList<string> PhotoUrls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// A page of a provider's reviews plus the summary over all of them. The summary is
/// deliberately whole-population, not page-scoped: it is the profile header's
/// "4.6 (23)", which must not change as the reader pages.
/// </summary>
public sealed record ProviderReviewListResult(
    ReviewSummary Summary,
    IReadOnlyList<ProviderReviewRow> Items,
    int Skip,
    int Take)
{
    public bool HasMore => Skip + Items.Count < Summary.ReviewCount;
}

public sealed record ProviderReviewQuery(
    Guid ProviderId,
    ReviewSortBy SortBy,
    Earnings.EarningsSortDirection SortDirection,
    int Skip,
    int Take);

/// <summary>
/// A pet parent's aggregate rating as given BY providers. The count travels with the
/// average because the two together are the honest claim: "5.0" off one rating and
/// "4.6" off forty mean very different things.
/// </summary>
public sealed record PetParentRatingSummary(int RatingCount, decimal? AverageRating)
{
    public static PetParentRatingSummary Empty { get; } = new(0, null);
}

/// <summary>
/// Submit-or-edit a review. <paramref name="ActorId"/> is the caller's own id —
/// their PetParentId for the parent direction, their ProviderId for the provider
/// direction — and is taken from the authenticated route, never from a request body.
/// <paramref name="Comment"/> is ignored for the provider direction.
/// </summary>
public sealed record SubmitBookingReviewCommand(
    string BookingType,
    Guid BookingId,
    string ReviewerType,
    Guid ActorId,
    int Rating,
    string? Comment);

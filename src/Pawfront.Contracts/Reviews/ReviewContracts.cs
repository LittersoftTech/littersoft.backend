namespace Pawfront.Contracts.Reviews;

/// <summary>
/// Body for the pet parent's review submit
/// (<c>POST /pet-parents/{petParentId}/bookings/{bookingId}/review</c> and its
/// night-stay twin). The booking, the author, and the direction all come from the
/// route — only the content travels here.
/// </summary>
/// <param name="Rating">1 to 5.</param>
/// <param name="Comment">
/// Optional free text, 1000 characters maximum. Blank or omitted stores nothing and
/// reads back as null, so "rated without writing anything" looks the same either way.
/// </param>
public sealed record SubmitBookingReviewRequest(int Rating, string? Comment);

/// <summary>
/// Body for the provider's rating of the pet parent
/// (<c>POST /providers/{providerId}/bookings/{bookingId}/rating</c> and its night-stay
/// twin). Rating only, by design — the provider direction carries no comment and no
/// photos.
/// </summary>
public sealed record SubmitParentRatingRequest(int Rating);

/// <summary>
/// A review as read back by its own author, after a submit or on the booking detail.
/// <see cref="Photos"/> is always empty for a provider-authored rating.
/// </summary>
/// <param name="ReviewerType">
/// <c>Parent</c> (the pet parent reviewing the provider) or <c>Provider</c> (the
/// provider rating the pet parent).
/// </param>
/// <param name="CreatedAtUtc">
/// When the review was first given. An edit does NOT move this — it is the date the
/// list sorts on and the app displays, and editing a rating is not writing a new one.
/// <see cref="UpdatedAtUtc"/> is what moves.
/// </param>
public sealed record BookingReviewResponse(
    Guid BookingReviewId,
    string BookingType,
    Guid BookingId,
    string ReviewerType,
    Guid ProviderId,
    Guid PetParentId,
    int Rating,
    string? Comment,
    IReadOnlyList<BookingReviewPhotoResponse> Photos,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BookingReviewPhotoResponse(
    Guid BookingReviewPhotoId,
    Guid BookingReviewId,
    string PhotoUrl,
    DateTimeOffset CreatedAtUtc);

/// <summary>A removed review photo.</summary>
public sealed record DeletedBookingReviewPhotoResponse(
    Guid BookingReviewPhotoId,
    Guid BookingReviewId,
    string PhotoUrl,
    DateTimeOffset DeletedAtUtc);

/// <summary>
/// The review block on a booking-detail read. Both fields describe the CALLER's own
/// side: the provider host reports the provider's rating of the parent, the parent host
/// the parent's review of the provider. A provider's private rating of a parent is
/// deliberately never returned on the parent host.
/// </summary>
/// <param name="CanReview">
/// True when this booking is in a state that admits a review — COMPLETED or PAID, and
/// an App booking (a Custom walk-in has no pet-parent record, so neither side can
/// review it). It stays true after a review exists, because a review can be edited.
/// </param>
/// <param name="MyReview">The caller's own review, or null if they have not written one.</param>
public sealed record BookingReviewDetailsSection(
    bool CanReview,
    BookingReviewResponse? MyReview);

/// <summary>
/// One review on a provider's public review list.
/// </summary>
/// <param name="BookingType">
/// <c>SingleDay</c> or <c>NightStay</c> — which booking the <see cref="BookingId"/>
/// refers to. The two kinds live in separate tables and share no id space, so the id
/// alone cannot say which detail screen to open.
/// </param>
/// <param name="JobId">
/// The friendly job reference (<c>PF-000123</c>), same as every other surface shows for
/// that booking.
/// </param>
/// <param name="ParentName">
/// Resolved live, so a parent who has deleted their account reads "Deleted User" rather
/// than leaving their real name frozen in the review.
/// </param>
public sealed record ProviderReviewItemResponse(
    Guid BookingReviewId,
    string BookingType,
    Guid BookingId,
    string? JobId,
    Guid PetParentId,
    string? ParentName,
    string? ParentPhotoUrl,
    int Rating,
    string? Comment,
    IReadOnlyList<string> PhotoUrls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// A page of a provider's reviews plus the summary over ALL of them — the summary is
/// the profile header's "4.6 (23)" and must not shift as the reader pages.
/// </summary>
public sealed record ProviderReviewsResponse(
    Guid ProviderId,
    ReviewSummaryResponse Summary,
    IReadOnlyList<ProviderReviewItemResponse> Reviews,
    int Skip,
    int Take,
    bool HasMore);

/// <summary>
/// Aggregate over a provider's received reviews. <see cref="AverageRating"/> is null
/// when <see cref="ReviewCount"/> is 0 — no reviews means no average, which should
/// render as "No reviews yet" rather than as 0.0. The star buckets are the usual
/// histogram.
/// </summary>
public sealed record ReviewSummaryResponse(
    int ReviewCount,
    decimal? AverageRating,
    int FiveStar,
    int FourStar,
    int ThreeStar,
    int TwoStar,
    int OneStar);

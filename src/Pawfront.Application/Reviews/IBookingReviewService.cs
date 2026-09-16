namespace Pawfront.Application.Reviews;

/// <summary>
/// Reviews exchanged between the two parties to a finished booking — the pet parent's
/// review of the provider (rating + comment + photos) and the provider's rating of the
/// pet parent (rating only).
/// </summary>
/// <remarks>
/// Host-agnostic: both directions and both booking kinds go through here, and the
/// caller supplies which by passing a <see cref="ReviewerTypes"/> and a
/// <see cref="ReviewedBookingTypes"/> value. That keeps the eligibility rule —
/// COMPLETED or PAID, correct party, App booking — in one place instead of four
/// near-copies across two hosts.
/// </remarks>
public interface IBookingReviewService
{
    /// <summary>
    /// Records or replaces one party's review. Resubmitting edits the existing row
    /// rather than adding a second, so a corrected star or a fixed typo does not
    /// duplicate. Photos are attached afterwards via
    /// <see cref="AddPhotoAsync"/> — the blob path is keyed by the review's own id,
    /// which does not exist until this returns.
    /// </summary>
    /// <exception cref="ReviewForbiddenException">Caller is not that party.</exception>
    /// <exception cref="BookingNotReviewableException">Not COMPLETED or PAID.</exception>
    /// <exception cref="ReviewNotAppBookingException">Custom walk-in.</exception>
    /// <exception cref="ArgumentException">Rating out of range, or comment too long.</exception>
    Task<BookingReviewRecord> SubmitAsync(
        SubmitBookingReviewCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// The review one party has written for a booking, or null if they have not
    /// written one. Null is the ordinary "not reviewed yet" case, not an error — it is
    /// what tells the app to show the prompt rather than the review.
    /// </summary>
    Task<BookingReviewRecord?> GetAsync(
        string bookingType,
        Guid bookingId,
        string reviewerType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attaches one uploaded photo to a parent's review. Scoped to the review's own
    /// author; the provider direction is rating-only and can never carry photos.
    /// </summary>
    /// <exception cref="BookingReviewNotFoundException">Unknown review, or not this author's.</exception>
    /// <exception cref="ReviewPhotoLimitReachedException">Already at the cap.</exception>
    Task<BookingReviewPhotoRecord> AddPhotoAsync(
        Guid bookingReviewId,
        Guid petParentId,
        string photoUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one photo from a parent's review, returning its URL so the caller can
    /// make a best-effort attempt at the blob. The review itself is not deletable, so
    /// this never strands a rating.
    /// </summary>
    /// <exception cref="BookingReviewPhotoNotFoundException">Unknown photo, wrong review, or not this author's.</exception>
    Task<DeletedBookingReviewPhoto> DeletePhotoAsync(
        Guid bookingReviewId,
        Guid bookingReviewPhotoId,
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The reviews pet parents have left for a provider, with the whole-population
    /// summary. <c>take</c> is capped at <see cref="ReviewLimits.MaxPageSize"/>.
    /// </summary>
    /// <remarks>
    /// There is no summary-only overload: the one surface that wants the headline
    /// figure (the provider's public profile) wants the newest reviews with it, and
    /// the summary here is whole-population regardless of the page, so it comes back
    /// from this call for free. A future search-card rating would need a *batched*
    /// by-provider-ids reader instead — a different shape, not this one.
    /// </remarks>
    Task<ProviderReviewListResult> ListForProviderAsync(
        ProviderReviewQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every rating a pet parent has authored, keyed by booking. One call serves a
    /// whole "my bookings" page — the alternative, a per-booking
    /// <see cref="GetAsync"/>, would be a lookup per card for a figure that is one
    /// small indexed read for all of them.
    /// </summary>
    /// <remarks>
    /// Deliberately returns ONLY the parent's own reviews. The provider's private
    /// rating of this parent is never surfaced on the parent host.
    /// </remarks>
    Task<IReadOnlyList<PetParentBookingRating>> ListRatingsByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader/writer behind <see cref="IBookingReviewService"/>. Validation
/// and the page cap live in the service; this layer only moves rows.
/// </summary>
public interface IBookingReviewStore
{
    Task<IReadOnlyList<PetParentBookingRating>> ListRatingsByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<BookingReviewRecord> UpsertAsync(
        SubmitBookingReviewCommand command,
        CancellationToken cancellationToken);

    Task<BookingReviewRecord?> GetAsync(
        string bookingType,
        Guid bookingId,
        string reviewerType,
        CancellationToken cancellationToken);

    Task<BookingReviewPhotoRecord> AddPhotoAsync(
        Guid bookingReviewId,
        Guid petParentId,
        string photoUrl,
        int maxPhotos,
        CancellationToken cancellationToken);

    Task<DeletedBookingReviewPhoto> DeletePhotoAsync(
        Guid bookingReviewId,
        Guid bookingReviewPhotoId,
        Guid petParentId,
        CancellationToken cancellationToken);

    Task<ProviderReviewListResult> ListForProviderAsync(
        ProviderReviewQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// A pet parent's aggregate rating as given by providers. Its own narrow interface
/// because the provider-facing customer card needs nothing else from the review
/// module, and because that card lives on the provider host while the rest of the
/// parent-review surface lives on the parent host.
/// </summary>
public interface IPetParentRatingReader
{
    Task<PetParentRatingSummary> GetAsync(Guid petParentId, CancellationToken cancellationToken);
}

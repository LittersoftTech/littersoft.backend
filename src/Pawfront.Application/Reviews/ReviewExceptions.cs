namespace Pawfront.Application.Reviews;

/// <summary>
/// The caller is not the party to this booking that the review direction requires —
/// they are neither its pet parent (parent direction) nor its provider (provider
/// direction). Maps to <b>403 Forbidden</b>, matching every other "you are not a
/// party to this booking" case. THROW 51301.
/// </summary>
public sealed class ReviewForbiddenException(Guid bookingId)
    : Exception($"You are not a party to booking '{bookingId}'.");

/// <summary>
/// The booking has not reached a reviewable state. Reviewable means <c>COMPLETED</c>
/// or <c>PAID</c>: PAID sits downstream of COMPLETED, so gating on COMPLETED alone
/// would close the review window the moment the provider recorded the payment —
/// which for cash is often immediate. Maps to <b>409 BookingNotReviewable</b>.
/// THROW 51302.
/// </summary>
public sealed class BookingNotReviewableException(Guid bookingId)
    : Exception($"Booking '{bookingId}' must be completed before it can be reviewed.");

/// <summary>
/// The booking is a Custom walk-in, which has no pet-parent record: there is nobody
/// to author the parent's review and nobody for the provider to rate. Maps to
/// <b>400 ReviewNotAppBooking</b>, mirroring <c>PaymentNotAppBooking</c> on the
/// mark-paid path. THROW 51303.
/// </summary>
public sealed class ReviewNotAppBookingException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is a private job and cannot be reviewed.");

/// <summary>
/// No review exists for this id under the calling author. Unknown id and "not yours"
/// are deliberately the same case, so this cannot be used to probe whether a review
/// exists — the same posture as <c>DeviceTokenNotFound</c>. Maps to
/// <b>404 ReviewNotFound</b>. THROW 51305.
/// </summary>
public sealed class BookingReviewNotFoundException(Guid bookingReviewId)
    : Exception($"Review '{bookingReviewId}' was not found.");

/// <summary>
/// The review already carries <see cref="ReviewLimits.MaxPhotos"/> photos. Maps to
/// <b>409 ReviewPhotoLimitReached</b>. THROW 51306.
/// </summary>
public sealed class ReviewPhotoLimitReachedException(Guid bookingReviewId, int maxPhotos)
    : Exception($"Review '{bookingReviewId}' already has the maximum of {maxPhotos} photos.");

/// <summary>
/// No such photo on that review for the calling author. Maps to
/// <b>404 ReviewPhotoNotFound</b>. THROW 51307.
/// </summary>
public sealed class BookingReviewPhotoNotFoundException(Guid bookingReviewPhotoId)
    : Exception($"Review photo '{bookingReviewPhotoId}' was not found.");

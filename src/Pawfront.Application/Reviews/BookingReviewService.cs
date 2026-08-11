namespace Pawfront.Application.Reviews;

/// <summary>
/// Orchestrator for booking reviews. Owns the input validation and the page cap; the
/// eligibility gate (booking exists, caller is that party, status is COMPLETED or
/// PAID, App booking) lives in <c>Review.UpsertBookingReview</c>, because it is a
/// decision about rows the database holds and belongs in the same transaction as the
/// write.
/// </summary>
public sealed class BookingReviewService(IBookingReviewStore store) : IBookingReviewService
{
    public Task<BookingReviewRecord> SubmitAsync(
        SubmitBookingReviewCommand command,
        CancellationToken cancellationToken)
    {
        if (command.BookingType is not (ReviewedBookingTypes.SingleDay or ReviewedBookingTypes.NightStay))
        {
            throw new ArgumentException(
                $"Unsupported bookingType '{command.BookingType}'.", nameof(command));
        }

        if (command.ReviewerType is not (ReviewerTypes.Parent or ReviewerTypes.Provider))
        {
            throw new ArgumentException(
                $"Unsupported reviewerType '{command.ReviewerType}'.", nameof(command));
        }

        if (command.Rating < ReviewLimits.MinRating || command.Rating > ReviewLimits.MaxRating)
        {
            throw new ArgumentException(
                $"Rating must be between {ReviewLimits.MinRating} and {ReviewLimits.MaxRating}.",
                nameof(command));
        }

        var comment = NormalizeComment(command.ReviewerType, command.Comment);

        return store.UpsertAsync(command with { Comment = comment }, cancellationToken);
    }

    public Task<BookingReviewRecord?> GetAsync(
        string bookingType,
        Guid bookingId,
        string reviewerType,
        CancellationToken cancellationToken)
        => store.GetAsync(bookingType, bookingId, reviewerType, cancellationToken);

    public Task<BookingReviewPhotoRecord> AddPhotoAsync(
        Guid bookingReviewId,
        Guid petParentId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            throw new ArgumentException("A photo URL is required.", nameof(photoUrl));
        }

        return store.AddPhotoAsync(
            bookingReviewId, petParentId, photoUrl.Trim(), ReviewLimits.MaxPhotos, cancellationToken);
    }

    public Task<DeletedBookingReviewPhoto> DeletePhotoAsync(
        Guid bookingReviewId,
        Guid bookingReviewPhotoId,
        Guid petParentId,
        CancellationToken cancellationToken)
        => store.DeletePhotoAsync(bookingReviewId, bookingReviewPhotoId, petParentId, cancellationToken);

    public Task<ProviderReviewListResult> ListForProviderAsync(
        ProviderReviewQuery query,
        CancellationToken cancellationToken)
    {
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0
            ? ReviewLimits.MaxPageSize
            : Math.Min(query.Take, ReviewLimits.MaxPageSize);

        return store.ListForProviderAsync(query with { Skip = skip, Take = take }, cancellationToken);
    }

    // Nothing to validate or cap: the parent is already established by the route
    // this is read through, and the result is bounded by how many bookings they
    // have reviewed.
    public Task<IReadOnlyList<PetParentBookingRating>> ListRatingsByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
        => store.ListRatingsByPetParentAsync(petParentId, cancellationToken);

    /// <summary>
    /// A provider rates and says nothing more, so their comment is dropped rather
    /// than rejected — the provider-side endpoint has no comment field, so a value
    /// here can only come from a direct caller, and the table CHECK would otherwise
    /// surface as an opaque constraint violation. A parent's blank comment becomes
    /// null, so "rated without writing anything" reads back identically whether the
    /// field was omitted or cleared.
    /// </summary>
    private static string? NormalizeComment(string reviewerType, string? comment)
    {
        if (reviewerType == ReviewerTypes.Provider)
        {
            return null;
        }

        var trimmed = comment?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > ReviewLimits.MaxCommentLength)
        {
            throw new ArgumentException(
                $"Review comment must be {ReviewLimits.MaxCommentLength} characters or fewer.",
                nameof(comment));
        }

        return trimmed;
    }
}

using System.Collections.Concurrent;
using Pawfront.Application.Earnings;
using Pawfront.Application.Reviews;

namespace Pawfront.Infrastructure.Sql.Reviews;

/// <summary>
/// Dev fallback for the in-memory configuration (no SQL connection string and Key
/// Vault disabled).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>NullProviderEarningsStore</c>, which reports zeros, this one actually
/// works: reviews are a WRITE flow, and a store that silently accepted a submit and
/// then never showed it would make the feature untestable without a database.
/// </para>
/// <para>
/// What it cannot do is enforce the eligibility gate. The booking's status, its
/// parties, and whether it is a Custom walk-in all live in the booking tables, which
/// this store does not see — so it accepts any submit. That is the same limitation the
/// other in-memory stores carry (they do not enforce capacity or closures either), and
/// it is why the real gate lives in <c>Review.UpsertBookingReview</c> rather than in
/// the Application layer.
/// </para>
/// </remarks>
internal sealed class InMemoryBookingReviewStore : IBookingReviewStore, IPetParentRatingReader
{
    private readonly ConcurrentDictionary<Guid, BookingReviewRecord> reviews = new();
    private readonly object writeLock = new();

    public Task<BookingReviewRecord> UpsertAsync(
        SubmitBookingReviewCommand command,
        CancellationToken cancellationToken)
    {
        lock (writeLock)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = reviews.Values.FirstOrDefault(r =>
                r.BookingType == command.BookingType
                && r.BookingId == command.BookingId
                && r.ReviewerType == command.ReviewerType);

            if (existing is not null)
            {
                var updated = existing with
                {
                    Rating = command.Rating,
                    Comment = command.Comment,
                    UpdatedAtUtc = now
                };
                reviews[updated.BookingReviewId] = updated;
                return Task.FromResult(updated);
            }

            // Without the booking tables there is no way to learn the real counterparty,
            // so the actor fills their own side and the other stays empty. Good enough
            // for exercising the endpoints; the SQL store is the one that gets this right.
            var isParent = command.ReviewerType == ReviewerTypes.Parent;
            var created = new BookingReviewRecord(
                BookingReviewId: Guid.NewGuid(),
                BookingType: command.BookingType,
                BookingId: command.BookingId,
                ReviewerType: command.ReviewerType,
                ProviderId: isParent ? Guid.Empty : command.ActorId,
                PetParentId: isParent ? command.ActorId : Guid.Empty,
                Rating: command.Rating,
                Comment: command.Comment,
                Photos: [],
                CreatedAtUtc: now,
                UpdatedAtUtc: now);

            reviews[created.BookingReviewId] = created;
            return Task.FromResult(created);
        }
    }

    public Task<BookingReviewRecord?> GetAsync(
        string bookingType,
        Guid bookingId,
        string reviewerType,
        CancellationToken cancellationToken)
        => Task.FromResult(reviews.Values.FirstOrDefault(r =>
            r.BookingType == bookingType
            && r.BookingId == bookingId
            && r.ReviewerType == reviewerType));

    public Task<BookingReviewPhotoRecord> AddPhotoAsync(
        Guid bookingReviewId,
        Guid petParentId,
        string photoUrl,
        int maxPhotos,
        CancellationToken cancellationToken)
    {
        lock (writeLock)
        {
            if (!reviews.TryGetValue(bookingReviewId, out var review)
                || review.ReviewerType != ReviewerTypes.Parent
                || review.PetParentId != petParentId)
            {
                throw new BookingReviewNotFoundException(bookingReviewId);
            }

            if (review.Photos.Count >= maxPhotos)
            {
                throw new ReviewPhotoLimitReachedException(bookingReviewId, maxPhotos);
            }

            var photo = new BookingReviewPhotoRecord(
                Guid.NewGuid(), bookingReviewId, photoUrl, DateTimeOffset.UtcNow);

            reviews[bookingReviewId] = review with { Photos = [.. review.Photos, photo] };
            return Task.FromResult(photo);
        }
    }

    public Task<DeletedBookingReviewPhoto> DeletePhotoAsync(
        Guid bookingReviewId,
        Guid bookingReviewPhotoId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        lock (writeLock)
        {
            if (!reviews.TryGetValue(bookingReviewId, out var review)
                || review.ReviewerType != ReviewerTypes.Parent
                || review.PetParentId != petParentId)
            {
                throw new BookingReviewPhotoNotFoundException(bookingReviewPhotoId);
            }

            var photo = review.Photos.FirstOrDefault(p => p.BookingReviewPhotoId == bookingReviewPhotoId)
                ?? throw new BookingReviewPhotoNotFoundException(bookingReviewPhotoId);

            reviews[bookingReviewId] = review with
            {
                Photos = [.. review.Photos.Where(p => p.BookingReviewPhotoId != bookingReviewPhotoId)]
            };

            return Task.FromResult(new DeletedBookingReviewPhoto(
                photo.BookingReviewPhotoId, bookingReviewId, photo.PhotoUrl, DateTimeOffset.UtcNow));
        }
    }

    public Task<ProviderReviewListResult> ListForProviderAsync(
        ProviderReviewQuery query,
        CancellationToken cancellationToken)
    {
        var all = reviews.Values
            .Where(r => r.ReviewerType == ReviewerTypes.Parent && r.ProviderId == query.ProviderId)
            .ToArray();

        var ascending = query.SortDirection == EarningsSortDirection.Ascending;
        var ordered = query.SortBy == ReviewSortBy.Rating
            ? (ascending
                ? all.OrderBy(r => r.Rating).ThenByDescending(r => r.CreatedAtUtc)
                : all.OrderByDescending(r => r.Rating).ThenByDescending(r => r.CreatedAtUtc))
            : (ascending
                ? all.OrderBy(r => r.CreatedAtUtc)
                : all.OrderByDescending(r => r.CreatedAtUtc));

        var items = ordered
            .ThenBy(r => r.BookingReviewId)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(r => new ProviderReviewRow(
                BookingReviewId: r.BookingReviewId,
                BookingType: r.BookingType,
                BookingId: r.BookingId,
                // No booking table here, so no job number to resolve.
                JobNumber: null,
                PetParentId: r.PetParentId,
                ParentName: null,
                ParentPhotoUrl: null,
                Rating: r.Rating,
                Comment: r.Comment,
                PhotoUrls: [.. r.Photos.Select(p => p.PhotoUrl)],
                CreatedAtUtc: r.CreatedAtUtc,
                UpdatedAtUtc: r.UpdatedAtUtc))
            .ToArray();

        return Task.FromResult(new ProviderReviewListResult(
            Summarize(all), items, query.Skip, query.Take));
    }

    public Task<PetParentRatingSummary> GetAsync(Guid petParentId, CancellationToken cancellationToken)
    {
        var ratings = reviews.Values
            .Where(r => r.ReviewerType == ReviewerTypes.Provider && r.PetParentId == petParentId)
            .Select(r => r.Rating)
            .ToArray();

        return Task.FromResult(ratings.Length == 0
            ? PetParentRatingSummary.Empty
            : new PetParentRatingSummary(
                ratings.Length, Math.Round((decimal)ratings.Average(), 2)));
    }

    private static ReviewSummary Summarize(IReadOnlyCollection<BookingReviewRecord> all)
        => all.Count == 0
            ? ReviewSummary.Empty
            : new ReviewSummary(
                ReviewCount: all.Count,
                AverageRating: Math.Round((decimal)all.Average(r => r.Rating), 2),
                FiveStar: all.Count(r => r.Rating == 5),
                FourStar: all.Count(r => r.Rating == 4),
                ThreeStar: all.Count(r => r.Rating == 3),
                TwoStar: all.Count(r => r.Rating == 2),
                OneStar: all.Count(r => r.Rating == 1));
}

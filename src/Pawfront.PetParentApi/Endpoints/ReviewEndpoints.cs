using Pawfront.Application.Bookings;
using Pawfront.Application.Reviews;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Reviews;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// The pet parent's side of reviews: rating a provider after a finished booking, with
/// an optional written comment and photos — plus the public read of a provider's
/// reviews, which is what the parent browses before booking.
/// </summary>
/// <remarks>
/// <para>
/// The submit routes sit on the ownership-filtered
/// <c>/pet-parents/{petParentId:guid}</c> group, so the author is resolved from the JWT
/// and a caller can only ever review their own booking. Whether the booking is theirs,
/// and whether it has reached COMPLETED or PAID, is settled inside
/// <c>Review.UpsertBookingReview</c> — in the same transaction as the write, so a
/// concurrent status change cannot slip past it.
/// </para>
/// <para>
/// Photos are a SECOND call after the review exists, matching every other gallery flow
/// in this codebase (pet photos, parent photos, booking evidence): the blob path is
/// keyed by the review's own id, which the submit is what mints.
/// </para>
/// <para>
/// <c>GET /providers/{providerId}/reviews</c> is deliberately NOT ownership-filtered —
/// it is someone else's public profile data, the same posture as the rest of
/// <c>/providers/*</c> on this host.
/// </para>
/// </remarks>
internal static class ReviewEndpoints
{
    private const long MaxPhotoBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    public static IEndpointRouteBuilder MapParentReviewEndpoints(this IEndpointRouteBuilder builder)
    {
        var singleDay = builder
            .MapGroup("/pet-parents/{petParentId:guid}/bookings/{bookingId:guid}/review")
            .RequireOwnedPetParent();

        singleDay.MapPost("/", SubmitSingleDayReview);
        singleDay.MapGet("/", GetSingleDayReview);
        singleDay.MapPost("/photos", UploadSingleDayReviewPhoto).DisableAntiforgery();
        singleDay.MapDelete("/photos/{photoId:guid}", DeleteSingleDayReviewPhoto);

        var nightStay = builder
            .MapGroup("/pet-parents/{petParentId:guid}/night-stay-bookings/{bookingId:guid}/review")
            .RequireOwnedPetParent();

        nightStay.MapPost("/", SubmitNightStayReview);
        nightStay.MapGet("/", GetNightStayReview);
        nightStay.MapPost("/photos", UploadNightStayReviewPhoto).DisableAntiforgery();
        nightStay.MapDelete("/photos/{photoId:guid}", DeleteNightStayReviewPhoto);

        // Public read: the reviews a provider has received.
        builder.MapGet("/providers/{providerId:guid}/reviews", ListProviderReviews);

        return builder;
    }

    private static Task<IResult> SubmitSingleDayReview(
        Guid petParentId,
        Guid bookingId,
        SubmitBookingReviewRequest request,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => SubmitAsync(
            ReviewedBookingTypes.SingleDay, petParentId, bookingId, request, reviewService, cancellationToken);

    private static Task<IResult> SubmitNightStayReview(
        Guid petParentId,
        Guid bookingId,
        SubmitBookingReviewRequest request,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => SubmitAsync(
            ReviewedBookingTypes.NightStay, petParentId, bookingId, request, reviewService, cancellationToken);

    /// <summary>
    /// Shared submit for both booking kinds. Resubmitting replaces the rating and
    /// comment on the same review rather than adding a second one, so this answers
    /// 200 on an edit and 201 only when the review is new.
    /// </summary>
    private static async Task<IResult> SubmitAsync(
        string bookingType,
        Guid petParentId,
        Guid bookingId,
        SubmitBookingReviewRequest request,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A rating is required.");
        }

        try
        {
            // Whether this is a first review or an edit decides 201 vs 200. Read before
            // the upsert, since afterwards the two are indistinguishable.
            var existing = await reviewService.GetAsync(
                bookingType, bookingId, ReviewerTypes.Parent, cancellationToken);

            var review = await reviewService.SubmitAsync(
                new SubmitBookingReviewCommand(
                    bookingType,
                    bookingId,
                    ReviewerTypes.Parent,
                    petParentId,
                    request.Rating,
                    request.Comment),
                cancellationToken);

            var response = ReviewResponseMapping.ToResponse(review);
            return existing is null
                ? ApiResults.Created(BuildLocation(bookingType, petParentId, bookingId), response)
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

    // No petParentId parameter: OwnedPetParentFilter reads it straight off the route
    // values, so the handler only declares what it actually uses.
    private static Task<IResult> GetSingleDayReview(
        Guid bookingId,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => GetAsync(ReviewedBookingTypes.SingleDay, bookingId, reviewService, cancellationToken);

    private static Task<IResult> GetNightStayReview(
        Guid bookingId,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => GetAsync(ReviewedBookingTypes.NightStay, bookingId, reviewService, cancellationToken);

    private static async Task<IResult> GetAsync(
        string bookingType,
        Guid bookingId,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        var review = await reviewService.GetAsync(
            bookingType, bookingId, ReviewerTypes.Parent, cancellationToken);

        // 404 rather than a null body: "you have not reviewed this" is the absence of
        // a resource at this URL. The booking detail's `review` section is the place
        // that reports it as a nullable field, since that read is about the booking.
        return review is null
            ? ApiResults.NotFound("ReviewNotFound", "You have not reviewed this booking.")
            : ApiResults.Ok(ReviewResponseMapping.ToResponse(review));
    }

    private static Task<IResult> UploadSingleDayReviewPhoto(
        Guid petParentId,
        Guid bookingId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => UploadPhotoAsync(
            ReviewedBookingTypes.SingleDay, petParentId, bookingId, file, blobStorage, reviewService, cancellationToken);

    private static Task<IResult> UploadNightStayReviewPhoto(
        Guid petParentId,
        Guid bookingId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => UploadPhotoAsync(
            ReviewedBookingTypes.NightStay, petParentId, bookingId, file, blobStorage, reviewService, cancellationToken);

    private static async Task<IResult> UploadPhotoAsync(
        string bookingType,
        Guid petParentId,
        Guid bookingId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        // Resolve the review FIRST: the blob path is keyed by its id, and uploading
        // before knowing the review exists would orphan a file on every stray call.
        var review = await reviewService.GetAsync(
            bookingType, bookingId, ReviewerTypes.Parent, cancellationToken);

        if (review is null)
        {
            return ApiResults.NotFound(
                "ReviewNotFound", "Submit your review before attaching photos to it.");
        }

        if (review.Photos.Count >= ReviewLimits.MaxPhotos)
        {
            // Checked here as well as in SQL so a caller at the cap gets the 409
            // without an upload being paid for first. SQL is what makes it race-safe.
            return ApiResults.Conflict(
                "ReviewPhotoLimitReached",
                $"A review can carry at most {ReviewLimits.MaxPhotos} photos.");
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.ReviewPhoto,
            review.BookingReviewId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var photo = await reviewService.AddPhotoAsync(
                review.BookingReviewId, petParentId, url, cancellationToken);

            return ApiResults.Created(
                $"{BuildLocation(bookingType, petParentId, bookingId)}/photos/{photo.BookingReviewPhotoId}",
                new BookingReviewPhotoResponse(
                    photo.BookingReviewPhotoId, photo.BookingReviewId, photo.PhotoUrl, photo.CreatedAtUtc));
        }
        catch (BookingReviewNotFoundException exception)
        {
            return ApiResults.NotFound("ReviewNotFound", exception.Message);
        }
        catch (ReviewPhotoLimitReachedException exception)
        {
            return ApiResults.Conflict("ReviewPhotoLimitReached", exception.Message);
        }
    }

    private static Task<IResult> DeleteSingleDayReviewPhoto(
        Guid petParentId,
        Guid bookingId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => DeletePhotoAsync(
            ReviewedBookingTypes.SingleDay, petParentId, bookingId, photoId, blobStorage, reviewService, cancellationToken);

    private static Task<IResult> DeleteNightStayReviewPhoto(
        Guid petParentId,
        Guid bookingId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
        => DeletePhotoAsync(
            ReviewedBookingTypes.NightStay, petParentId, bookingId, photoId, blobStorage, reviewService, cancellationToken);

    private static async Task<IResult> DeletePhotoAsync(
        string bookingType,
        Guid petParentId,
        Guid bookingId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IBookingReviewService reviewService,
        CancellationToken cancellationToken)
    {
        var review = await reviewService.GetAsync(
            bookingType, bookingId, ReviewerTypes.Parent, cancellationToken);

        if (review is null)
        {
            return ApiResults.NotFound("ReviewPhotoNotFound", $"Review photo '{photoId}' was not found.");
        }

        try
        {
            var deleted = await reviewService.DeletePhotoAsync(
                review.BookingReviewId, photoId, petParentId, cancellationToken);

            // Best-effort blob cleanup: the SQL row (source of truth) is already gone,
            // so a storage hiccup must not fail the request — the blob is merely
            // orphaned for a future sweep.
            try
            {
                await blobStorage.DeleteAsync(deleted.PhotoUrl, cancellationToken);
            }
            catch
            {
                // Swallow — see above.
            }

            return ApiResults.Ok(new DeletedBookingReviewPhotoResponse(
                deleted.BookingReviewPhotoId, deleted.BookingReviewId, deleted.PhotoUrl, deleted.DeletedAtUtc));
        }
        catch (BookingReviewPhotoNotFoundException exception)
        {
            return ApiResults.NotFound("ReviewPhotoNotFound", exception.Message);
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

    private static string BuildLocation(string bookingType, Guid petParentId, Guid bookingId)
    {
        var segment = bookingType == ReviewedBookingTypes.NightStay ? "night-stay-bookings" : "bookings";
        return $"/api/v1/pet-parents/{petParentId}/{segment}/{bookingId}/review";
    }

    private static IResult? ValidatePhotoFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        if (file.Length > MaxPhotoBytes)
        {
            return ApiResults.BadRequest(
                "ImageTooLarge", $"Photo must be {MaxPhotoBytes / (1024 * 1024)} MB or smaller.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType)
            || !AllowedPhotoContentTypes.Contains(file.ContentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat", "Photo must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }
}

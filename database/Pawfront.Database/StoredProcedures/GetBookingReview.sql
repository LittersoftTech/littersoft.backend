-- Reads one party's review of a booking, if they have written one. Used for the
-- read-back endpoint and for the "own review" section appended to the two
-- booking-detail reads, so the app can tell "not reviewed yet" (prompt) from
-- "already reviewed" (show it, allow an edit).
--
-- Takes no actor: the caller's identity is already established by the route the
-- review is read through, and @ReviewerType alone says whose review is wanted.
--
-- Returns TWO result sets: the review row (EMPTY when none exists — that is the
-- ordinary case, not an error), then its photos.
CREATE OR ALTER PROCEDURE [Review].[GetBookingReview]
    @BookingType NVARCHAR(16),      -- 'SingleDay' | 'NightStay'
    @BookingId UNIQUEIDENTIFIER,
    @ReviewerType NVARCHAR(16)      -- 'Parent' | 'Provider'
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BookingReviewId UNIQUEIDENTIFIER;

    SELECT @BookingReviewId = [BookingReviewId]
    FROM [Review].[BookingReviews]
    WHERE [BookingType] = @BookingType
      AND [BookingId] = @BookingId
      AND [ReviewerType] = @ReviewerType;

    SELECT [BookingReviewId],
           [BookingType],
           [BookingId],
           [ReviewerType],
           [ProviderId],
           [PetParentId],
           [Rating],
           [Comment],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Review].[BookingReviews]
    WHERE [BookingReviewId] = @BookingReviewId;

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewId] = @BookingReviewId
    ORDER BY [CreatedAtUtc] ASC, [BookingReviewPhotoId] ASC;
END;

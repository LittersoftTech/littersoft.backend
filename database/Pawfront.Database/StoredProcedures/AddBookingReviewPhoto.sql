-- Records one photo against a pet parent's booking review (the blob upload happens
-- in the app layer; this stores the resulting URL). Scoped to the review's own
-- author: a provider-direction review is rating-only and can never carry photos.
--
-- The per-review cap is enforced HERE rather than only in C# because the count is a
-- race: two uploads in flight would each read four existing photos and both insert.
-- UPDLOCK + HOLDLOCK on the count makes them serialise.
--
-- THROWs: 51305 review not found for this author (unknown id and "not yours" are
-- deliberately the same case, so it cannot be used to probe whether a review
-- exists), 51306 the photo cap for this review is already reached.
CREATE OR ALTER PROCEDURE [Review].[AddBookingReviewPhoto]
    @BookingReviewId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
    @MaxPhotos INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Existing INT;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Review].[BookingReviews] WITH (UPDLOCK, HOLDLOCK)
        WHERE [BookingReviewId] = @BookingReviewId
          AND [PetParentId] = @PetParentId
          AND [ReviewerType] = N'Parent')
    BEGIN
        -- SET XACT_ABORT ON rolls the transaction back, as elsewhere in this codebase.
        THROW 51305, 'Review was not found for this pet parent.', 1;
    END

    SELECT @Existing = COUNT(*)
    FROM [Review].[BookingReviewPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingReviewId] = @BookingReviewId;

    IF @Existing >= @MaxPhotos
    BEGIN
        THROW 51306, 'This review already has the maximum number of photos.', 1;
    END

    DECLARE @Inserted TABLE ([BookingReviewPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Review].[BookingReviewPhotos] ([BookingReviewId], [PhotoUrl])
    OUTPUT inserted.[BookingReviewPhotoId] INTO @Inserted
    VALUES (@BookingReviewId, @PhotoUrl);

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewPhotoId] = (SELECT TOP (1) [BookingReviewPhotoId] FROM @Inserted);

    COMMIT TRANSACTION;
END;

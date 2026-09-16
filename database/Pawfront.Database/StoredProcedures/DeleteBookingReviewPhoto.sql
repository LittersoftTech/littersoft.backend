-- Removes one photo from a pet parent's booking review, scoped by review + author
-- so a caller can only ever delete their own. The row here is the source of truth;
-- the app layer makes a best-effort attempt at the blob afterwards, which is why the
-- deleted [PhotoUrl] is returned.
--
-- The review itself is NOT deletable — only its photos — so this never leaves a
-- rating stranded.
--
-- THROWs: 51307 photo not found for this review and author (unknown id, wrong
-- review, and "not yours" are deliberately one case).
CREATE OR ALTER PROCEDURE [Review].[DeleteBookingReviewPhoto]
    @BookingReviewId UNIQUEIDENTIFIER,
    @BookingReviewPhotoId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = p.[PhotoUrl]
    FROM [Review].[BookingReviewPhotos] p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN [Review].[BookingReviews] r
        ON r.[BookingReviewId] = p.[BookingReviewId]
    WHERE p.[BookingReviewPhotoId] = @BookingReviewPhotoId
      AND p.[BookingReviewId] = @BookingReviewId
      AND r.[PetParentId] = @PetParentId
      AND r.[ReviewerType] = N'Parent';

    IF @PhotoUrl IS NULL
    BEGIN
        -- SET XACT_ABORT ON rolls the transaction back, as elsewhere in this codebase.
        THROW 51307, 'Review photo was not found.', 1;
    END

    DELETE FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewPhotoId] = @BookingReviewPhotoId;

    SELECT [BookingReviewPhotoId] = @BookingReviewPhotoId,
           [BookingReviewId] = @BookingReviewId,
           [PhotoUrl] = @PhotoUrl,
           [DeletedAtUtc] = SYSUTCDATETIME();

    COMMIT TRANSACTION;
END;

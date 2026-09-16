-- Photos attached to a pet parent's booking review. One row per uploaded photo —
-- same shape as [Booking].[BookingEvidence] and [Provider].[ProviderPhotos].
--
-- The blob upload happens in the app layer under the [ReviewPhotos] folder
-- ("review-photos/<bookingReviewId>/<guid>.<ext>"), which is why photos are a
-- SECOND call after the review row exists: the blob owner id is the review's own
-- id. The row here is the source of truth; a delete removes the row and makes a
-- best-effort attempt at the blob.
--
-- Only parent-authored reviews can carry photos ([Review].[AddBookingReviewPhoto]
-- rejects the provider direction, which has no comment or photos by design).
CREATE TABLE [Review].[BookingReviewPhotos]
(
    [BookingReviewPhotoId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingReviewPhotos_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingReviewId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingReviewPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingReviewPhotos] PRIMARY KEY CLUSTERED ([BookingReviewPhotoId] ASC),
    CONSTRAINT [FK_BookingReviewPhotos_BookingReviews_BookingReviewId]
        FOREIGN KEY ([BookingReviewId]) REFERENCES [Review].[BookingReviews] ([BookingReviewId])
        ON DELETE CASCADE
);

GO

-- Photos are always read for a known review (or a page of them), oldest-first.
CREATE INDEX [IX_BookingReviewPhotos_Review_Created]
    ON [Review].[BookingReviewPhotos] ([BookingReviewId], [CreatedAtUtc] ASC)
    INCLUDE ([PhotoUrl]);

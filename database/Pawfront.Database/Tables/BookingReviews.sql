-- Reviews exchanged between the two parties to a finished booking. ONE table
-- holds both directions, discriminated by [ReviewerType]:
--   'Parent'   -> the pet parent reviewing the provider: rating 1-5, an optional
--                 written comment, and optional photos (child table
--                 [Review].[BookingReviewPhotos]).
--   'Provider' -> the provider rating the pet parent: rating ONLY. The CHECK
--                 below enforces that a provider row carries no comment, and no
--                 photo rows are ever written for one.
--
-- [BookingType] discriminates which booking table [BookingId] points at:
-- 'SingleDay' -> [Booking].[Bookings], 'NightStay' -> [Booking].[NightStayBookings].
-- As with [Booking].[BookingPayments], there is deliberately NO FK on
-- [BookingId] — one column cannot reference two tables. [Review].[UpsertBookingReview]
-- is what proves the booking exists, that the caller is a party to it, and that it
-- has reached COMPLETED or PAID.
--
-- Both party ids are always stored, whichever direction the review runs in (one is
-- the author, the other the subject), so the read paths need no CASE to work out
-- whose review this is. [PetParentId] is NOT NULL because a review requires an App
-- booking — a Custom walk-in has no pet-parent record to author or receive one.
--
-- Names are NOT denormalised here: the list read joins [Parent].[PetParents] live,
-- so an anonymised account correctly reads "Deleted User" rather than keeping the
-- real name frozen in a review row.
CREATE TABLE [Review].[BookingReviews]
(
    [BookingReviewId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingReviews_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingType] NVARCHAR(16) NOT NULL,
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [ReviewerType] NVARCHAR(16) NOT NULL,
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [Rating] TINYINT NOT NULL,
    -- Free text, parent direction only. 1000 characters, matching the app's
    -- counter; blank input is stored as NULL by the sproc rather than as ''.
    [Comment] NVARCHAR(1000) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingReviews_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingReviews_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingReviews] PRIMARY KEY CLUSTERED ([BookingReviewId] ASC),
    -- One review per booking per direction. Resubmitting edits the existing row
    -- (the submit endpoint is an upsert), so this is the constraint that makes a
    -- concurrent double-submit safe rather than duplicating.
    CONSTRAINT [UQ_BookingReviews_Booking_Reviewer]
        UNIQUE ([BookingType], [BookingId], [ReviewerType]),
    CONSTRAINT [CK_BookingReviews_BookingType]
        CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_BookingReviews_ReviewerType]
        CHECK ([ReviewerType] IN (N'Parent', N'Provider')),
    CONSTRAINT [CK_BookingReviews_Rating]
        CHECK ([Rating] >= 1 AND [Rating] <= 5),
    -- A provider rates the parent 1-5 and says nothing more.
    CONSTRAINT [CK_BookingReviews_ProviderRatingHasNoComment]
        CHECK ([ReviewerType] = N'Parent' OR [Comment] IS NULL)
);

GO

-- Drives the provider-reviews list + its average/histogram summary. Filtered to
-- the parent direction because that is the only one this read ever wants, which
-- keeps provider-authored parent ratings out of the index entirely.
CREATE INDEX [IX_BookingReviews_Provider_Created]
    ON [Review].[BookingReviews] ([ProviderId], [CreatedAtUtc] DESC)
    INCLUDE ([Rating], [BookingType], [BookingId], [PetParentId])
    WHERE [ReviewerType] = N'Parent';

GO

-- The mirror: a pet parent's aggregate rating, as shown on the provider-facing
-- customer card.
CREATE INDEX [IX_BookingReviews_PetParent]
    ON [Review].[BookingReviews] ([PetParentId])
    INCLUDE ([Rating])
    WHERE [ReviewerType] = N'Provider';

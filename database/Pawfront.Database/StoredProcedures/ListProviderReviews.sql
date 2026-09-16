-- The reviews pet parents have left for a provider: the list behind the provider's
-- public profile and their own "my reviews" screen. Paginated (the API caps @Take at
-- 20), sortable by the date the review was given or by the rating score, either
-- direction.
--
-- Returns THREE result sets:
--   1. Summary over ALL the provider's reviews (not just this page) — count,
--      average, and the 1-5 histogram, so the header reads "4.6 (23)" without a
--      second call.
--   2. The page itself, ordered.
--   3. The photos belonging to that page's reviews, so the client needs no per-review
--      follow-up (an N+1 across a page of reviews is the thing to avoid here).
--
-- Only parent-authored rows are considered: a provider's ratings OF parents are the
-- other direction and belong on the customer card, not here. The filtered index
-- [IX_BookingReviews_Provider_Created] matches that predicate exactly.
--
-- The parent's name and photo are joined LIVE rather than denormalised onto the
-- review, so a parent who deletes their account correctly reads "Deleted User"
-- instead of leaving their real name frozen in every review they ever wrote.
CREATE OR ALTER PROCEDURE [Review].[ListProviderReviews]
    @ProviderId UNIQUEIDENTIFIER,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Rating'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Summary. With no reviews at all, AVG yields NULL and every count is 0 —
    -- which is exactly what the profile should show for a provider nobody has
    -- reviewed yet, so there is no special-casing to do.
    -- The SUMs are COALESCEd because SUM over ZERO rows is NULL, not 0 — the counts
    -- must stay non-null integers so the reader needs no null check per bucket. The
    -- average is deliberately left nullable (see above).
    SELECT
        [ReviewCount]   = COUNT(*),
        [AverageRating] = CAST(AVG(CAST([Rating] AS DECIMAL(9, 4))) AS DECIMAL(3, 2)),
        [FiveStar]      = COALESCE(SUM(CASE WHEN [Rating] = 5 THEN 1 ELSE 0 END), 0),
        [FourStar]      = COALESCE(SUM(CASE WHEN [Rating] = 4 THEN 1 ELSE 0 END), 0),
        [ThreeStar]     = COALESCE(SUM(CASE WHEN [Rating] = 3 THEN 1 ELSE 0 END), 0),
        [TwoStar]       = COALESCE(SUM(CASE WHEN [Rating] = 2 THEN 1 ELSE 0 END), 0),
        [OneStar]       = COALESCE(SUM(CASE WHEN [Rating] = 1 THEN 1 ELSE 0 END), 0)
    FROM [Review].[BookingReviews]
    WHERE [ProviderId] = @ProviderId
      AND [ReviewerType] = N'Parent';

    -- Resolve the page's ids once, so result sets 2 and 3 describe the same rows
    -- even under a concurrent insert, and so the photo read is a join rather than a
    -- repeat of the ordering logic.
    DECLARE @Page TABLE (
        [Ordinal] INT NOT NULL PRIMARY KEY,
        [BookingReviewId] UNIQUEIDENTIFIER NOT NULL UNIQUE);

    INSERT INTO @Page ([Ordinal], [BookingReviewId])
    SELECT ordered.[Ordinal], ordered.[BookingReviewId]
    FROM (
        SELECT
            r.[BookingReviewId],
            [Ordinal] = ROW_NUMBER() OVER (ORDER BY
                -- Rating, when that is what was asked for.
                CASE WHEN @SortBy = N'Rating' AND @SortDirection = N'Asc'  THEN r.[Rating] END ASC,
                CASE WHEN @SortBy = N'Rating' AND @SortDirection = N'Desc' THEN r.[Rating] END DESC,
                -- The date given: the primary key for @SortBy = 'Date', and the
                -- secondary for a rating sort — newest first within a star band,
                -- which is what a reader scanning "all the 5s" expects.
                CASE WHEN @SortBy = N'Date' AND @SortDirection = N'Asc' THEN r.[CreatedAtUtc] END ASC,
                CASE WHEN @SortBy = N'Date' AND @SortDirection = N'Asc' THEN NULL
                     ELSE r.[CreatedAtUtc] END DESC,
                -- Deterministic tie-break. Without it, reviews sharing a timestamp
                -- (or a score) can order differently between two calls, which makes
                -- OFFSET paging repeat or skip rows.
                r.[BookingReviewId])
        FROM [Review].[BookingReviews] r
        WHERE r.[ProviderId] = @ProviderId
          AND r.[ReviewerType] = N'Parent'
    ) ordered
    WHERE ordered.[Ordinal] > @Skip
      AND ordered.[Ordinal] <= @Skip + @Take;

    -- 2. The page.
    SELECT
        r.[BookingReviewId],
        r.[BookingType],
        r.[BookingId],
        -- Raw job number; the 'PF-000123' label is formatted in C# exactly as the
        -- booking-detail read does, so the two surfaces show the same id. NULL only
        -- if the booking row has since gone (it does not: bookings are retained
        -- through both account deletes).
        [JobNumber] = COALESCE(b.[JobNumber], n.[JobNumber]),
        r.[PetParentId],
        [ParentName] = pp.[FirstName] + N' ' + pp.[LastName],
        [ParentPhotoUrl] = pp.[ProfilePhotoUrl],
        r.[Rating],
        r.[Comment],
        r.[CreatedAtUtc],
        r.[UpdatedAtUtc]
    FROM @Page pg
    INNER JOIN [Review].[BookingReviews] r
        ON r.[BookingReviewId] = pg.[BookingReviewId]
    LEFT JOIN [Booking].[Bookings] b
        ON r.[BookingType] = N'SingleDay' AND b.[BookingId] = r.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON r.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = r.[BookingId]
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = r.[PetParentId]
    ORDER BY pg.[Ordinal] ASC;

    -- 3. That page's photos, grouped in C# by BookingReviewId.
    SELECT
        p.[BookingReviewPhotoId],
        p.[BookingReviewId],
        p.[PhotoUrl],
        p.[CreatedAtUtc]
    FROM @Page pg
    INNER JOIN [Review].[BookingReviewPhotos] p
        ON p.[BookingReviewId] = pg.[BookingReviewId]
    ORDER BY pg.[Ordinal] ASC, p.[CreatedAtUtc] ASC, p.[BookingReviewPhotoId] ASC;
END;

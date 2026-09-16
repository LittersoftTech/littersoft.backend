-- A pet parent's aggregate rating, as given BY providers they have booked with.
-- Feeds the [Rating] field on the provider-facing customer card
-- (GET /pet-parents/{petParentId}/details on the provider host), which was wired
-- ahead of this feature and had always returned null.
--
-- The provider direction is rating-only, so there is no comment or photo to read —
-- just the average and the count. The count matters as much as the average: "5.0"
-- off one rating and "4.6" off forty are very different claims, and the card should
-- be able to say which it is.
--
-- Always returns exactly ONE row. A parent nobody has rated yet gets a NULL average
-- and a count of 0 rather than an empty result set, so the caller needs no
-- no-rows branch.
CREATE OR ALTER PROCEDURE [Review].[GetPetParentRatingSummary]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        [RatingCount]   = COUNT(*),
        [AverageRating] = CAST(AVG(CAST([Rating] AS DECIMAL(9, 4))) AS DECIMAL(3, 2))
    FROM [Review].[BookingReviews]
    WHERE [PetParentId] = @PetParentId
      AND [ReviewerType] = N'Provider';
END;
